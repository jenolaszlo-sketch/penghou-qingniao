namespace Penghou.Qingniao;

/// <summary>Raised when an in-memory coordinator cannot recover its private runtime state.</summary>
internal sealed class DelegationCoordinatorStateUnavailableException : InvalidOperationException
{
    internal DelegationCoordinatorStateUnavailableException(DelegationId delegationId)
        : base($"Delegation '{delegationId}' has no in-memory coordinator state.") => DelegationId = delegationId;

    internal DelegationId DelegationId { get; }
}

/// <summary>
/// A deterministic in-memory coordinator for one bounded Implement operation.
/// The coordinator is intentionally internal: its private phase and provider
/// adapter state are execution authority, not public progress contracts.
/// </summary>
internal sealed class InMemoryDelegationCoordinator
{
    private const string AgentProtocolVersion = "qingniao-inmemory-v1";
    private const string AgentCapability = "agent.execute";

    // Cancellation observation and cancellation calls are safety work: they
    // must still be possible after ordinary worker/duration budgets expire.
    // They are nevertheless bounded so a provider that never reaches a
    // terminal state cannot turn repeated pumps into an unbounded call loop.
    internal const int CancellationSafetyCallLimit = 8;

    private readonly object stateGate = new();
    private readonly Dictionary<DelegationId, RuntimeState> states = new();
    private readonly SemaphoreSlim acceptanceGate = new(1, 1);
    private readonly IDelegationAcceptanceRegistry acceptanceRegistry;
    private readonly IWorkflowPlanResolver planResolver;
    private readonly IProviderRegistry providerRegistry;
    private readonly InMemoryExternalOperationProviderCatalog adapterCatalog;
    private readonly InMemoryExternalOperationHandleCaptureRegistry handleRegistry;
    private readonly InMemoryDelegationExecutionStore executionStore;
    private readonly SimingExternalOperationSemanticFingerprintVerifier fingerprintVerifier;
    private readonly ISupervisorContextProvider? contextProvider;
    private readonly ISupervisorInterventionAcceptanceRegistry interventionRegistry;
    private readonly ICandidateRevisionPublicationRegistry? candidateRegistry;
    private readonly IDeterministicCandidateValidator? candidateValidator;
    private readonly IIndependentCandidateReviewer? candidateReviewer;
    private readonly ICandidateCorrector? candidateCorrector;
    private readonly Func<DateTimeOffset> now;

    internal InMemoryDelegationCoordinator(
        IDelegationAcceptanceRegistry acceptanceRegistry,
        IWorkflowPlanResolver planResolver,
        IProviderRegistry providerRegistry,
        InMemoryExternalOperationProviderCatalog adapterCatalog,
        SimingExternalOperationSemanticFingerprintVerifier fingerprintVerifier,
        InMemoryExternalOperationHandleCaptureRegistry? handleRegistry = null,
        InMemoryDelegationExecutionStore? executionStore = null,
        Func<DateTimeOffset>? now = null,
        ISupervisorContextProvider? contextProvider = null,
        ISupervisorInterventionAcceptanceRegistry? interventionRegistry = null,
        ICandidateRevisionPublicationRegistry? candidateRegistry = null,
        IDeterministicCandidateValidator? candidateValidator = null,
        IIndependentCandidateReviewer? candidateReviewer = null,
        ICandidateCorrector? candidateCorrector = null)
    {
        this.acceptanceRegistry = acceptanceRegistry ?? throw new ArgumentNullException(nameof(acceptanceRegistry));
        this.planResolver = planResolver ?? throw new ArgumentNullException(nameof(planResolver));
        this.providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));
        this.adapterCatalog = adapterCatalog ?? throw new ArgumentNullException(nameof(adapterCatalog));
        this.fingerprintVerifier = fingerprintVerifier ?? throw new ArgumentNullException(nameof(fingerprintVerifier));
        this.handleRegistry = handleRegistry ?? new InMemoryExternalOperationHandleCaptureRegistry();
        this.executionStore = executionStore ?? new InMemoryDelegationExecutionStore();
        this.now = now ?? (() => DateTimeOffset.UtcNow);
        this.contextProvider = contextProvider;
        this.interventionRegistry = interventionRegistry ?? new InMemorySupervisorInterventionAcceptanceRegistry();
        this.candidateRegistry = candidateRegistry ?? new InMemoryCandidateRevisionPublicationRegistry();
        this.candidateValidator = candidateValidator;
        this.candidateReviewer = candidateReviewer;
        this.candidateCorrector = candidateCorrector;
    }

    /// <summary>Accepts a request and publishes its initial queued snapshot.</summary>
    internal async ValueTask<DelegationAcceptance> AcceptAsync(
        DelegationCallerScope caller,
        DelegationRequest request,
        CancellationToken cancellationToken = default)
    {
        await acceptanceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await AcceptCoreAsync(caller, request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            acceptanceGate.Release();
        }
    }

    private async ValueTask<DelegationAcceptance> AcceptCoreAsync(
        DelegationCallerScope caller,
        DelegationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);

        var resolution = planResolver.Resolve(caller, request);
        if (!resolution.HasBuiltInStructure || resolution.BoundRequest is null)
        {
            throw new InvalidOperationException(
                "The in-memory coordinator executes only the verified built-in Implement plan.");
        }

        var acceptance = await acceptanceRegistry.AcceptAsync(
            caller,
            resolution.BoundRequest!,
            cancellationToken).ConfigureAwait(false);

        lock (stateGate)
        {
            if (!acceptance.IsNew)
            {
                if (states.ContainsKey(acceptance.DelegationId))
                {
                    return acceptance;
                }
            }
        }

        return await InitializeRuntimeAsync(acceptance, resolution, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<DelegationAcceptance> InitializeRuntimeAsync(
        DelegationAcceptance acceptance,
        WorkflowPlanResolution resolution,
        CancellationToken cancellationToken)
    {
        var providerSnapshot = providerRegistry.GetSnapshot();
        var selection = providerSnapshot.Select(
            new ProviderSelectionRequest([new CapabilityRequirement(AgentCapability, 1)]));
        var match = selection.Match;
        IExternalOperationProvider? adapter = null;
        string? providerFailure = null;
        if (match is null)
        {
            providerFailure = "No compatible provider is registered for the agent.execute capability.";
        }
        else
        {
            var lookup = adapterCatalog.Lookup(match);
            if (!lookup.IsFound)
            {
                providerFailure = lookup.Status == ProviderAdapterLookupStatus.Unauthorized
                    ? $"Provider '{match.Provider.Provider}' is not authorized for execution."
                    : $"Provider '{match.Provider.Provider}' has no executable adapter.";
            }
            else
            {
                adapter = lookup.Adapter;
            }
        }

        DateTimeOffset acceptedAt;
        try
        {
            var existing = await executionStore.GetAsync(
                acceptance.DelegationId,
                cancellationToken).ConfigureAwait(false);
            if (existing.Progress.State != DelegationState.Queued || existing.Result is not null)
            {
                throw new DelegationCoordinatorStateUnavailableException(acceptance.DelegationId);
            }

            acceptedAt = existing.Progress.UpdatedAt;
        }
        catch (DelegationExecutionNotFoundException)
        {
            acceptedAt = RequireNow();
            await executionStore.CreateAsync(
                acceptance.DelegationId,
                acceptedAt,
                cancellationToken).ConfigureAwait(false);
        }

        var runtime = new RuntimeState(
            acceptance.DelegationId,
            resolution.BoundRequest!,
            resolution,
            providerSnapshot,
            match,
            adapter,
            providerFailure,
            fingerprintVerifier,
            acceptedAt);

        lock (stateGate)
        {
            states.TryAdd(acceptance.DelegationId, runtime);
        }

        return acceptance;
    }

    /// <summary>Reads the immutable observable execution snapshot.</summary>
    internal ValueTask<DelegationExecutionSnapshot> GetAsync(
        DelegationId delegationId,
        CancellationToken cancellationToken = default) =>
        executionStore.GetAsync(delegationId, cancellationToken);

    /// <summary>
    /// Requests durable cancellation at an expected observable revision. The
    /// caller token only transports this operation; it is never interpreted as
    /// a durable cancellation request.
    /// </summary>
    internal async ValueTask<DelegationExecutionSnapshot> CancelAsync(
        DelegationId delegationId,
        long expectedRevision,
        string cancellationKey,
        string reason,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var runtime = GetRuntime(delegationId);
        await runtime.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationKey = ExternalOperationCancellationValidation.RequireKey(cancellationKey);
            reason = ExternalOperationCancellationValidation.RequireReason(reason);
            var current = await executionStore.GetAsync(delegationId, cancellationToken).ConfigureAwait(false);
            var replayingCancellation = runtime.CancellationKey is not null;
            if (replayingCancellation)
            {
                if (!string.Equals(runtime.CancellationKey, cancellationKey, StringComparison.Ordinal)
                    || !string.Equals(runtime.CancellationReason, reason, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "A delegation already has a different durable cancellation request.");
                }

                if (runtime.CancellationReceipt is not null
                    || runtime.CancellationIntentOnly
                    || DelegationLifecycle.IsTerminal(current.Progress.State))
                {
                    return current;
                }
            }

            if (DelegationLifecycle.IsTerminal(current.Progress.State))
            {
                return current;
            }

            if (!replayingCancellation)
            {
                EnsureExpectedRevision(delegationId, current, expectedRevision);
            }

            runtime.CancellationKey = cancellationKey;
            runtime.CancellationReason = reason;
            runtime.EvaluationCancellation?.Cancel();
            runtime.CorrectionCancellation?.Cancel();
            if (current.Progress.State == DelegationState.Queued)
            {
                runtime.CancellationIntentOnly = false;
                runtime.Phase = CoordinatorPhase.Complete;
                return await PublishTerminalAsync(
                    runtime,
                    current,
                    DelegationState.Cancelled,
                    "Cancellation was requested before provider execution began.",
                    [],
                    cancellationToken).ConfigureAwait(false);
            }

            if (runtime.Handle is null)
            {
                runtime.CancellationIntentOnly = true;
                runtime.Phase = CoordinatorPhase.Start;
                return current;
            }

            runtime.CancellationRequest = new ExternalOperationCancelRequest(
                runtime.Handle,
                cancellationKey,
                reason);
            runtime.CancellationIntentOnly = false;
            return await CancelKnownHandleAsync(runtime, current, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            runtime.Gate.Release();
        }
    }

    /// <summary>Requests durable cancellation using an explicit provider request.</summary>
    internal async ValueTask<DelegationExecutionSnapshot> CancelAsync(
        DelegationId delegationId,
        long expectedRevision,
        ExternalOperationCancelRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        var runtime = GetRuntime(delegationId);
        await runtime.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await executionStore.GetAsync(delegationId, cancellationToken).ConfigureAwait(false);

            // An explicit request carries a provider handle.  Validate its
            // execution identity even while queued or terminal; otherwise a
            // foreign handle could become a durable cancellation alias.
            if (!HandleMatchesRuntime(runtime, request.Handle))
            {
                throw new InvalidOperationException(
                    "A cancellation request must name a handle from this exact execution correlation.");
            }

            // Idempotent control replays are recognized before the caller's
            // revision fence. A transport retry may arrive with an older
            // cached revision after another observer has advanced the state.
            var replayingCancellation = runtime.CancellationKey is not null;
            if (replayingCancellation)
            {
                if (!string.Equals(runtime.CancellationKey, request.CancellationKey, StringComparison.Ordinal)
                    || !string.Equals(runtime.CancellationReason, request.Reason, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "A delegation already has a different durable cancellation request.");
                }

                if (runtime.CancellationRequest is not null
                    && !CancellationRequestsEqual(runtime.CancellationRequest, request))
                {
                    throw new InvalidOperationException(
                        "A durable cancellation request cannot change its provider handle.");
                }

                if (runtime.CancellationReceipt is not null
                    || runtime.CancellationIntentOnly
                    || DelegationLifecycle.IsTerminal(current.Progress.State))
                {
                    return current;
                }
            }

            if (DelegationLifecycle.IsTerminal(current.Progress.State))
            {
                return current;
            }

            if (!replayingCancellation)
            {
                EnsureExpectedRevision(delegationId, current, expectedRevision);
            }

            if (current.Progress.State == DelegationState.Queued)
            {
                runtime.CancellationRequest = request;
                runtime.CancellationKey = request.CancellationKey;
                runtime.CancellationReason = request.Reason;
                runtime.EvaluationCancellation?.Cancel();
                runtime.CorrectionCancellation?.Cancel();
                runtime.CancellationIntentOnly = false;
                runtime.Phase = CoordinatorPhase.Complete;
                return await PublishTerminalAsync(
                    runtime,
                    current,
                    DelegationState.Cancelled,
                    "Cancellation was requested before provider execution began.",
                    [],
                    cancellationToken).ConfigureAwait(false);
            }

            if (runtime.Handle is null)
            {
                // The start call may have been accepted before its response
                // was lost. Preserve the cancellation identity and let the
                // next fenced pump retry the exact StartIdentity to recover a
                // handle; local state must remain non-terminal meanwhile.
                runtime.CancellationRequest = null;
                runtime.CancellationKey ??= request.CancellationKey;
                runtime.CancellationReason ??= request.Reason;
                runtime.CancellationIntentOnly = true;
                runtime.Phase = CoordinatorPhase.Start;
                return current;
            }

            if (request.Handle != runtime.Handle)
            {
                throw new InvalidOperationException(
                    "A cancellation request must name the coordinator's exact captured handle.");
            }

            runtime.CancellationRequest = request;
            runtime.CancellationKey = request.CancellationKey;
            runtime.CancellationReason = request.Reason;
            runtime.EvaluationCancellation?.Cancel();
            runtime.CorrectionCancellation?.Cancel();
            runtime.CancellationIntentOnly = false;
            return await CancelKnownHandleAsync(runtime, current, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            runtime.Gate.Release();
        }
    }

    /// <summary>Resumes one observed waiting provider operation at a revision fence.</summary>
    internal ValueTask<DelegationExecutionSnapshot> ResumeAsync(
        DelegationId delegationId,
        long expectedRevision,
        string resumeKey,
        IReadOnlyList<DelegationArtifactReference>? correctionArtifacts = null,
        string? reason = null,
        CancellationToken cancellationToken = default) =>
        ResumeAsync(
            delegationId,
            expectedRevision,
            new ExternalOperationResumeRequest(
                ResolveResumeHandleForRequest(delegationId, resumeKey),
                resumeKey,
                correctionArtifacts,
                reason),
            cancellationToken);

    /// <summary>Resumes one observed waiting provider operation.</summary>
    internal async ValueTask<DelegationExecutionSnapshot> ResumeAsync(
        DelegationId delegationId,
        long expectedRevision,
        ExternalOperationResumeRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        var runtime = GetRuntime(delegationId);
        await runtime.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ResumeCoreAsync(runtime, expectedRevision, request, authorizedIntervention: false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            runtime.Gate.Release();
        }
    }

    private async ValueTask<DelegationExecutionSnapshot> ResumeCoreAsync(
        RuntimeState runtime,
        long expectedRevision,
        ExternalOperationResumeRequest request,
        bool authorizedIntervention,
        CancellationToken cancellationToken)
    {
        var current = await executionStore.GetAsync(runtime.DelegationId, cancellationToken).ConfigureAwait(false);
        if (!authorizedIntervention)
        {
            throw new InvalidOperationException(
                "Direct resume is not a supervisory authorization; apply an accepted Approve intervention instead.");
        }
        var replayingResume = runtime.ResumeRequest is not null;
        if (replayingResume)
        {
            if (!ResumeRequestsEqual(runtime.ResumeRequest!, request))
            {
                throw new InvalidOperationException("A delegation already has a different durable resume request.");
            }

            if (runtime.ResumeReceipt is not null)
            {
                return current;
            }

            // A captured rotated handle is durable acceptance evidence. Exact
            // replay observes it instead of issuing a duplicate ResumeAsync.
            if (runtime.ResumeAcceptedAmbiguity)
            {
                if (runtime.ResumeAmbiguityObserved)
                {
                    return current;
                }

                runtime.ResumeAmbiguityObserved = true;
                runtime.Phase = CoordinatorPhase.Observe;
                return await PumpObserveAsync(runtime, current, cancellationToken).ConfigureAwait(false);
            }
        }

        if (!replayingResume)
        {
            EnsureExpectedRevision(runtime.DelegationId, current, expectedRevision);
        }
        if (DelegationLifecycle.IsTerminal(current.Progress.State))
        {
            return current;
        }

        if (runtime.Handle is null)
        {
            throw new InvalidOperationException("A resume request requires a known captured provider handle.");
        }

        if (!replayingResume && request.Handle != runtime.Handle)
        {
            throw new InvalidOperationException("A resume request must name the coordinator's exact captured handle.");
        }

        if (runtime.LastObservation?.State != ExternalOperationState.Waiting)
        {
            throw new InvalidOperationException("This coordinator slice can resume only a known non-terminal provider Waiting state.");
        }

        if (!replayingResume)
        {
            runtime.ResumeRequest = request;
            runtime.ResumePreviousHandle = runtime.Handle;
        }

        var durationExceeded = await EnforceDurationAsync(runtime, current, cancellationToken).ConfigureAwait(false);
        if (durationExceeded is not null)
        {
            return durationExceeded;
        }

        var retryingResume = runtime.ResumeAttempted;
        if (current.Progress.WorkerCalls >= runtime.Request.Budget.MaximumWorkerCalls
            || (retryingResume && current.Progress.Retries >= runtime.Request.Budget.MaximumRetries))
        {
            runtime.Phase = CoordinatorPhase.Complete;
            return await PublishTerminalAsync(runtime, current, DelegationState.Failed,
                "The provider resume retry budget was exhausted.", [], cancellationToken).ConfigureAwait(false);
        }

        runtime.ResumeAttempted = true;
        current = await PublishRunningAsync(runtime, current,
            checked(current.Progress.WorkerCalls + 1),
            retryingResume ? checked(current.Progress.Retries + 1) : current.Progress.Retries,
            cancellationToken).ConfigureAwait(false);
        runtime.WakeHint = null;
        runtime.CheckpointId = null;

        return await ResumeKnownHandleAsync(runtime, current, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Activates the current waiting checkpoint for a host-authenticated supervisor.</summary>
    internal async ValueTask ActivateCheckpointAsync(
        DelegationId delegationId,
        SupervisorIdentity supervisor,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(supervisor);
        var runtime = GetRuntime(delegationId);
        await runtime.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await executionStore.GetAsync(delegationId, cancellationToken).ConfigureAwait(false);
            if (current.Progress.State != DelegationState.WaitingForSupervisor
                || current.Progress.Checkpoint is null)
            {
                throw new SupervisorInterventionRejectedException(
                    SupervisorInterventionRejectionReason.CheckpointNotActive,
                    "Only the current waiting checkpoint can be activated.");
            }

            await interventionRegistry.ActivateAsync(supervisor, current.Progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            runtime.Gate.Release();
        }
    }

    // Short host-facing aliases keep the internal seam convenient for the
    // in-memory host while the public transport API remains deferred.
    internal ValueTask ActivateAsync(
        DelegationId delegationId,
        SupervisorIdentity supervisor,
        CancellationToken cancellationToken = default) =>
        ActivateCheckpointAsync(delegationId, supervisor, cancellationToken);

    internal ValueTask ActivateCheckpointAsync(
        SupervisorIdentity supervisor,
        DelegationId delegationId,
        CancellationToken cancellationToken = default) =>
        ActivateCheckpointAsync(delegationId, supervisor, cancellationToken);

    /// <summary>Reads the non-authorizing wake hint published for a waiting operation.</summary>
    internal async ValueTask<WakeHint?> GetWakeHintAsync(
        DelegationId delegationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var runtime = GetRuntime(delegationId);
        await runtime.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await executionStore.GetAsync(delegationId, cancellationToken).ConfigureAwait(false);
            var hint = runtime.WakeHint;
            if (hint is null
                || current.Progress.State != DelegationState.WaitingForSupervisor
                || current.Progress.Checkpoint is null
                || hint.CheckpointId != current.Progress.Checkpoint.CheckpointId
                || hint.AfterRevision != current.Progress.Revision
                || hint.ExpiresAt is null
                || hint.ExpiresAt <= RequireNow())
            {
                runtime.WakeHint = null;
                return null;
            }

            return hint;
        }
        finally
        {
            runtime.Gate.Release();
        }
    }

    /// <summary>Gets bounded context for the exact current waiting checkpoint fence.</summary>
    internal async ValueTask<SupervisorContextPackage> GetContextAsync(
        SupervisorIdentity supervisor,
        SupervisorContextRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentNullException.ThrowIfNull(request);
        var runtime = GetRuntime(request.DelegationId);
        await runtime.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        DelegationExecutionSnapshot fenced;
        try
        {
            if (contextProvider is null)
            {
                throw new InvalidOperationException("No supervisor context provider is configured for this coordinator.");
            }

            fenced = await executionStore.GetAsync(request.DelegationId, cancellationToken).ConfigureAwait(false);
            request.ValidateAgainst(fenced.Progress);
        }
        finally
        {
            runtime.Gate.Release();
        }

        // Context providers are trusted host dependencies, but may be
        // asynchronous and non-reentrant. Do not hold the runtime gate while
        // invoking one; the exact fence is revalidated after the callback.
        var package = await contextProvider!.GetAsync(supervisor, request, cancellationToken).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(package);

        await runtime.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await executionStore.GetAsync(request.DelegationId, cancellationToken).ConfigureAwait(false);
            request.ValidateAgainst(current.Progress);
            package.ValidateAgainst(request, current.Progress);
            return package;
        }
        finally
        {
            runtime.Gate.Release();
        }
    }

    internal ValueTask<SupervisorContextPackage> GetSupervisorContextAsync(
        SupervisorIdentity supervisor,
        SupervisorContextRequest request,
        CancellationToken cancellationToken = default) =>
        GetContextAsync(supervisor, request, cancellationToken);

    internal ValueTask<SupervisorContextPackage> GetContextAsync(
        DelegationId delegationId,
        SupervisorIdentity supervisor,
        SupervisorContextRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.DelegationId != delegationId)
        {
            throw new ArgumentException("The context request delegation does not match the supplied delegation.", nameof(request));
        }

        return GetContextAsync(supervisor, request, cancellationToken);
    }

    /// <summary>Accepts and applies the single supported Approve intervention.</summary>
    internal async ValueTask<DelegationExecutionSnapshot> ApplyInterventionAsync(
        SupervisorIdentity supervisor,
        SupervisorIntervention intervention,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentNullException.ThrowIfNull(intervention);
        // Reject unsupported actions before touching the acceptance registry;
        // rejection must not claim the checkpoint or consume its key.
        if (intervention.Action is not SupervisorAction.Approve)
        {
            throw new NotSupportedException(
                $"Supervisor action '{intervention.Action.Kind}' is not supported by this coordinator slice.");
        }

        var runtime = GetRuntime(intervention.DelegationId);
        await runtime.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await executionStore.GetAsync(intervention.DelegationId, cancellationToken).ConfigureAwait(false);
            var exactAcceptedReplay = runtime.AcceptedIntervention is not null
                && InterventionsEqual(runtime.AcceptedIntervention, intervention);
            if (exactAcceptedReplay && current.Progress.State != DelegationState.WaitingForSupervisor)
            {
                if (DelegationLifecycle.IsTerminal(current.Progress.State))
                {
                    return current;
                }

                if (runtime.ResumeRequest is null)
                {
                    throw new InvalidOperationException("The accepted approval has no pending resume intent.");
                }

                return await ResumeCoreAsync(
                    runtime,
                    intervention.ExpectedRevision,
                    runtime.ResumeRequest,
                    authorizedIntervention: true,
                    cancellationToken).ConfigureAwait(false);
            }

            if (current.Progress.State != DelegationState.WaitingForSupervisor
                || current.Progress.Checkpoint is null)
            {
                throw new SupervisorInterventionRejectedException(
                    SupervisorInterventionRejectionReason.CheckpointNotActive,
                    "A supervisor intervention requires the current waiting checkpoint.");
            }

            if (current.Progress.Checkpoint.DelegationId != intervention.DelegationId
                || current.Progress.Checkpoint.CheckpointId != intervention.CheckpointId)
            {
                throw new SupervisorInterventionRejectedException(
                    SupervisorInterventionRejectionReason.UnauthorizedCheckpoint,
                    "The intervention does not target the current checkpoint.");
            }

            if (current.Progress.Revision != intervention.ExpectedRevision)
            {
                throw new SupervisorInterventionRejectedException(
                    SupervisorInterventionRejectionReason.StaleRevision,
                    "The intervention expected revision does not match the current waiting fence.");
            }

            await interventionRegistry.AcceptAsync(supervisor, intervention, cancellationToken).ConfigureAwait(false);
            if (runtime.Handle is null)
            {
                throw new InvalidOperationException("An approval requires a captured provider handle.");
            }

            var approve = (SupervisorAction.Approve)intervention.Action;
            // Persist the pending resume intent immediately after acceptance.
            // This also reconstructs the intent for an exact acceptance replay
            // if a cancellation/fault interrupted the first application.
            var resumeRequest = runtime.ResumeRequest ?? new ExternalOperationResumeRequest(
                    runtime.Handle,
                    intervention.InterventionKey,
                    correctionArtifacts: [],
                    reason: approve.Rationale);
            if (runtime.ResumeRequest is null)
            {
                runtime.ResumeRequest = resumeRequest;
                runtime.ResumePreviousHandle = runtime.Handle;
            }
            runtime.AcceptedIntervention ??= intervention;

            return await ResumeCoreAsync(
                runtime,
                intervention.ExpectedRevision,
                resumeRequest,
                authorizedIntervention: true,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            runtime.Gate.Release();
        }
    }

    internal ValueTask<DelegationExecutionSnapshot> InterveneAsync(
        SupervisorIdentity supervisor,
        SupervisorIntervention intervention,
        CancellationToken cancellationToken = default) =>
        ApplyInterventionAsync(supervisor, intervention, cancellationToken);

    internal ValueTask<DelegationExecutionSnapshot> ApplyInterventionAsync(
        DelegationId delegationId,
        SupervisorIdentity supervisor,
        SupervisorIntervention intervention,
        CancellationToken cancellationToken = default)
    {
        if (intervention.DelegationId != delegationId)
        {
            throw new ArgumentException("The intervention delegation does not match the supplied delegation.", nameof(intervention));
        }

        return ApplyInterventionAsync(supervisor, intervention, cancellationToken);
    }

    /// <summary>
    /// Advances exactly one logical operation after an expected revision. A
    /// stale revision is rejected so concurrent callers cannot silently pump
    /// the same execution twice.
    /// </summary>
    internal async ValueTask<DelegationExecutionSnapshot> PumpAsync(
        DelegationId delegationId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var runtime = GetRuntime(delegationId);
        await runtime.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var gateHeld = true;
        try
        {
            var current = await executionStore.GetAsync(delegationId, cancellationToken).ConfigureAwait(false);
            if (current.Progress.Revision != expectedRevision)
            {
                // A concurrent duplicate pump may arrive after its sibling
                // has already published the immutable terminal pair. Return
                // that exact terminal replay instead of turning harmless
                // duplicate work into a second evaluator attempt.
                if (DelegationLifecycle.IsTerminal(current.Progress.State))
                {
                    return current;
                }

                throw new DelegationExecutionStaleException(
                    delegationId,
                    current.Progress.Revision,
                    expectedRevision);
            }

            if (DelegationLifecycle.IsTerminal(current.Progress.State))
            {
                return current;
            }

            // A provider Waiting observation is a stable supervisory
            // checkpoint. Ordinary pumps are intentionally no-ops until a
            // host-authenticated intervention resumes the operation.
            if (current.Progress.State == DelegationState.WaitingForSupervisor)
            {
                return current;
            }

            var pumped = runtime.Phase switch
            {
                CoordinatorPhase.Start => await PumpStartAsync(runtime, current, cancellationToken).ConfigureAwait(false),
                CoordinatorPhase.Observe => await PumpObserveAsync(runtime, current, cancellationToken).ConfigureAwait(false),
                CoordinatorPhase.GetResult => await PumpResultAsync(runtime, current, cancellationToken).ConfigureAwait(false),
                CoordinatorPhase.Evaluate => current,
                _ => throw new InvalidOperationException("The in-memory coordinator has an unknown private phase."),
            };

            // Candidate evaluators are deliberately invoked outside the
            // per-runtime gate.  This permits an evaluator to perform
            // independent reads or callbacks without deadlocking a pump.
            if (runtime.Phase == CoordinatorPhase.Evaluate)
            {
                runtime.Gate.Release();
                gateHeld = false;
                return await RunEvaluationAndCorrectionAsync(runtime, delegationId).ConfigureAwait(false);
            }

            return pumped;
        }
        finally
        {
            if (gateHeld)
            {
                runtime.Gate.Release();
            }
        }
    }

    /// <summary>
    /// Runs the shared evaluation task and, when policy permits, the one
    /// shared correction task. The caller token is deliberately not used for
    /// either task: transport cancellation must not poison durable work.
    /// </summary>
    private async Task<DelegationExecutionSnapshot> RunEvaluationAndCorrectionAsync(
        RuntimeState runtime,
        DelegationId delegationId)
    {
        var gateHeld = false;
        try
        {
            while (true)
            {
                if (gateHeld)
                {
                    runtime.Gate.Release();
                    gateHeld = false;
                }

                Task<CandidateEvaluationOutcome> evaluation;
                lock (runtime.EvaluationSync)
                {
                    runtime.EvaluationCancellation ??= new CancellationTokenSource();
                    runtime.EvaluationTask ??= EvaluateCandidateAsync(runtime, runtime.EvaluationCancellation.Token);
                    evaluation = runtime.EvaluationTask;
                }

                var outcome = await evaluation.ConfigureAwait(false);
                await runtime.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                gateHeld = true;
                var latest = await executionStore.GetAsync(delegationId, CancellationToken.None).ConfigureAwait(false);
                if (DelegationLifecycle.IsTerminal(latest.Progress.State))
                {
                    return latest;
                }

                if (runtime.CancellationKey is not null && IsCancellationConfirmed(runtime))
                {
                    return await PublishEvaluationCancellationAsync(runtime, latest, outcome).ConfigureAwait(false);
                }

                if (runtime.CancellationKey is not null
                    && !runtime.CancellationReconciled)
                {
                    return latest;
                }

                if (runtime.Phase != CoordinatorPhase.Evaluate)
                {
                    return latest;
                }

                var terminal = await HandleEvaluationOutcomeAsync(runtime, latest, outcome).ConfigureAwait(false);
                if (terminal is not null)
                {
                    return terminal;
                }

                // CorrectionStarted is reserved while the gate is held. No
                // competing pump can create a second correction invocation.
                var correction = runtime.CorrectionTask!;
                runtime.Gate.Release();
                gateHeld = false;
                CandidateCorrectionOutcome corrected;
                Exception? correctionFailure = null;
                try
                {
                    corrected = await correction.ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    corrected = null!;
                    correctionFailure = exception;
                }

                await runtime.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                gateHeld = true;
                latest = await executionStore.GetAsync(delegationId, CancellationToken.None).ConfigureAwait(false);
                if (DelegationLifecycle.IsTerminal(latest.Progress.State))
                {
                    return latest;
                }

                if (runtime.CancellationKey is not null && IsCancellationConfirmed(runtime))
                {
                    return await PublishEvaluationCancellationAsync(runtime, latest, null).ConfigureAwait(false);
                }

                if (runtime.CancellationKey is not null
                    && !runtime.CancellationReconciled)
                {
                    return latest;
                }

                if (runtime.CancellationKey is not null && runtime.EvaluationCycle == 1)
                {
                    return await PublishEvaluationTerminalAsync(runtime, latest, outcome, CancellationToken.None)
                        .ConfigureAwait(false);
                }

                if (runtime.EvaluationCycle == 2)
                {
                    // Another concurrent pump completed correction and
                    // reserved the fresh evaluator pair. Join that task.
                    continue;
                }

                if (correctionFailure is not null)
                {
                    runtime.Phase = CoordinatorPhase.Complete;
                    return await PublishTerminalAsync(
                        runtime,
                        latest,
                        DelegationState.Failed,
                        $"Candidate correction failed: {NormalizeFailure(correctionFailure.Message)}",
                        runtime.ResultArtifacts ?? [],
                        CancellationToken.None,
                        latest.Progress.WorkerCalls + 4).ConfigureAwait(false);
                }

                try
                {
                    await PublishCorrectedCandidateAsync(runtime, corrected).ConfigureAwait(false);
                    runtime.EvaluationCycle = 2;
                    runtime.ValidationFailure = null;
                    runtime.ReviewFailure = null;
                    runtime.ValidationInvocationId = $"validation:{runtime.DelegationId.Value:D}:2";
                    runtime.ReviewInvocationId = $"review:{runtime.DelegationId.Value:D}:2";
                    runtime.EvaluationTask = null;
                    runtime.EvaluationCancellation = new CancellationTokenSource();
                    runtime.Phase = CoordinatorPhase.Evaluate;
                }
                catch (Exception exception)
                {
                    // Leave Candidate and ResultArtifacts pointing at the
                    // immutable generation-1 publication on any bad output.
                    runtime.Phase = CoordinatorPhase.Complete;
                    return await PublishTerminalAsync(
                        runtime,
                        latest,
                        DelegationState.Failed,
                        $"Candidate correction validation failed: {NormalizeFailure(exception.Message)}",
                        runtime.ResultArtifacts ?? [],
                        CancellationToken.None,
                        latest.Progress.WorkerCalls + 4).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (gateHeld)
            {
                runtime.Gate.Release();
            }
        }
    }

    private async ValueTask<DelegationExecutionSnapshot> PublishEvaluationCancellationAsync(
        RuntimeState runtime,
        DelegationExecutionSnapshot current,
        CandidateEvaluationOutcome? outcome)
    {
        if (outcome is not null)
        {
            RecordEvaluationEvidence(runtime, outcome);
        }

        runtime.AggregateEvidence = new EvidenceBundle(runtime.Invocations, runtime.Validations, runtime.Reviews);
        runtime.Phase = CoordinatorPhase.Complete;
        return await PublishTerminalAsync(
            runtime,
            current,
            DelegationState.Cancelled,
            "Cancellation was requested while candidate evaluation was in progress.",
            runtime.ResultArtifacts ?? [],
            CancellationToken.None,
            ActualWorkerCalls(runtime, current)).ConfigureAwait(false);
    }

    private async ValueTask<DelegationExecutionSnapshot?> HandleEvaluationOutcomeAsync(
        RuntimeState runtime,
        DelegationExecutionSnapshot current,
        CandidateEvaluationOutcome outcome)
    {
        if (runtime.EvaluationCycle == 1 && runtime.CorrectionStarted)
        {
            // A duplicate pump is represented by the shared correction task;
            // do not record or publish the generation-1 outcome twice.
            return null;
        }

        RecordEvaluationEvidence(runtime, outcome);
        runtime.AggregateEvidence = new EvidenceBundle(runtime.Invocations, runtime.Validations, runtime.Reviews);

        var validationPass = outcome.Validation is not null && IsPass(outcome.Validation.Outcome);
        var reviewApprove = outcome.Review is not null && IsApprove(outcome.Review.Outcome);
        var explicitFailureOrRejection = runtime.ValidationFailure is null
            && runtime.ReviewFailure is null
            && outcome.Validation is not null
            && outcome.Review is not null
            && runtime.CancellationKey is null
            && (!validationPass || !reviewApprove);
        if (runtime.EvaluationCycle == 1 && explicitFailureOrRejection)
        {
            if (candidateCorrector is not null)
            {
                const int correctionCalls = 1;
                var evaluatorCalls = ConfiguredEvaluatorCallCount();
                var projectedWorkerCalls = checked(current.Progress.WorkerCalls + 1 + evaluatorCalls + correctionCalls + evaluatorCalls);
                if (projectedWorkerCalls <= runtime.Request.Budget.MaximumWorkerCalls)
                {
                    runtime.CorrectionStarted = true;
                    runtime.CorrectionCancellation ??= new CancellationTokenSource();
                    runtime.CorrectionTask ??= CorrectCandidateAsync(runtime, outcome, runtime.CorrectionCancellation.Token);
                    return null;
                }

                runtime.Phase = CoordinatorPhase.Complete;
                var consumed = ActualWorkerCalls(runtime, current);
                var budgetOutcome = new BudgetExceededOutcome(
                    runtime.DelegationId,
                    "qingniao-runtime-budget-v1",
                    new BudgetCharge("worker-calls", BudgetQuantity.Count(consumed)),
                    BudgetQuantity.Count(current.Progress.WorkerCalls),
                    BudgetQuantity.Count(consumed),
                    runtime.DelegationId.Value,
                    $"The correction call and fresh evaluator pair exceed the remaining worker-call budget (required {projectedWorkerCalls}, limit {runtime.Request.Budget.MaximumWorkerCalls}).",
                    Later(current.Progress.UpdatedAt, RequireNow()));
                return await PublishTerminalAsync(
                    runtime,
                    current,
                    DelegationState.BudgetExceeded,
                    budgetOutcome.Reason,
                    runtime.ResultArtifacts ?? [],
                    CancellationToken.None,
                    consumed,
                    budgetOutcome).ConfigureAwait(false);
            }
        }

        return await PublishEvaluationTerminalAsync(runtime, current, outcome, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private void RecordEvaluationEvidence(RuntimeState runtime, CandidateEvaluationOutcome outcome)
    {
        if (outcome.Validation is not null
            && !runtime.Validations.Any(existing => string.Equals(existing.Invocation.Attempt.AttemptId, outcome.Validation.Invocation.Attempt.AttemptId, StringComparison.Ordinal)))
        {
            runtime.Validations.Add(outcome.Validation);
        }

        if (outcome.Review is not null
            && !runtime.Reviews.Any(existing => string.Equals(existing.Invocation.Attempt.AttemptId, outcome.Review.Invocation.Attempt.AttemptId, StringComparison.Ordinal)))
        {
            runtime.Reviews.Add(outcome.Review);
        }

        if (runtime.ImplementationInvocation is not null
            && !runtime.Invocations.Any(invocation => string.Equals(invocation.Attempt.AttemptId, runtime.ImplementationInvocation.Attempt.AttemptId, StringComparison.Ordinal)))
        {
            runtime.Invocations.Add(runtime.ImplementationInvocation);
        }
    }

    private async Task<CandidateCorrectionOutcome> CorrectCandidateAsync(
        RuntimeState runtime,
        CandidateEvaluationOutcome outcome,
        CancellationToken cancellationToken)
    {
        var findings = new List<EvidenceFinding>();
        if (outcome.Validation is not null)
        {
            findings.AddRange(outcome.Validation.Findings);
        }

        if (outcome.Review is not null)
        {
            findings.AddRange(outcome.Review.Findings);
        }

        var targetGeneration = new NodeGenerationId(Guid.NewGuid());
        var targetCorrelation = new ExternalOperationCorrelation(
            runtime.Correlation.DelegationId,
            runtime.Correlation.WorkflowRun,
            runtime.Correlation.StructuralNode,
            targetGeneration,
            $"correction-{runtime.DelegationId.Value:D}",
            runtime.Correlation.Agent,
            runtime.Correlation.Task);
        runtime.CorrectionGeneration = targetGeneration;
        runtime.CorrectionCorrelation = targetCorrelation;
        var request = new CandidateCorrectionRequest(
            runtime.InitialCandidate!,
            outcome.Validation,
            outcome.Review,
            findings,
            targetGeneration,
            checked(runtime.InitialCandidate!.Revision + 1),
            $"correction:{runtime.DelegationId.Value:D}:1",
            targetCorrelation,
            $"correction:{runtime.DelegationId.Value:D}:1");
        runtime.CorrectionInvocationStarted = true;
        return await candidateCorrector!.CorrectAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask PublishCorrectedCandidateAsync(RuntimeState runtime, CandidateCorrectionOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var candidate = outcome.Candidate ?? throw new InvalidOperationException("The corrector did not return a candidate.");
        var source = runtime.InitialCandidate ?? throw new InvalidOperationException("The initial candidate is unavailable.");
        if (candidate.DelegationId != source.DelegationId
            || candidate.StructuralNode != source.StructuralNode
            || candidate.CandidateId != source.CandidateId
            || candidate.Revision != checked(source.Revision + 1)
            || candidate.NodeGeneration == source.NodeGeneration
            || candidate.NodeGeneration != runtime.CorrectionGeneration)
        {
            throw new InvalidOperationException("The corrected candidate must preserve ownership and CandidateId while advancing exactly one revision and generation.");
        }

        if (candidate.Artifacts.Count == 0
            || candidate.ContentIdentity == source.ContentIdentity
            || candidate.Artifacts.Any(artifact => source.Artifacts.Any(existing =>
                ArtifactSame(existing, artifact) || existing.ContentIdentity == artifact.ContentIdentity)))
        {
            throw new InvalidOperationException("The corrected candidate must contain new artifacts.");
        }

        var invocation = outcome.ImplementationInvocation ?? throw new InvalidOperationException("The corrector did not return implementation evidence.");
        if (!CandidateRevisionIdentity.SubjectEqual(candidate, invocation.Candidate)
            || invocation.NodeGeneration != candidate.NodeGeneration
            || invocation.Attempt.AttemptId == runtime.ImplementationInvocation!.Attempt.AttemptId
            || runtime.CorrectionCorrelation is null
            || invocation.ExecutionCorrelation is null
            || !CorrelationEqual(invocation.ExecutionCorrelation, runtime.CorrectionCorrelation))
        {
            throw new InvalidOperationException("Corrected implementation evidence does not identify the exact fresh candidate and attempt.");
        }

        if (candidateRegistry is null)
        {
            throw new InvalidOperationException("Candidate correction requires a publication registry.");
        }

        var publication = await candidateRegistry.PublishAsync(candidate, CancellationToken.None).ConfigureAwait(false);
        if (!CandidateRevisionIdentity.SemanticallyEqual(publication.Candidate, candidate))
        {
            throw new InvalidOperationException("The candidate publication registry returned different corrected content.");
        }

        runtime.Candidate = publication.Candidate;
        if (candidate.Artifacts.Any(artifact => artifact.DelegationId != candidate.DelegationId
            || artifact.StructuralNode != candidate.StructuralNode
            || artifact.NodeGeneration != candidate.NodeGeneration))
        {
            throw new InvalidOperationException("Corrected implementation artifacts must belong to the corrected candidate generation.");
        }

        runtime.ResultArtifacts = candidate.Artifacts.ToArray();
        runtime.ImplementationInvocation = invocation;
        runtime.Invocations.Add(invocation);
    }

    private static bool ArtifactSame(DelegationArtifactReference left, DelegationArtifactReference right) =>
        string.Equals(left.Provider, right.Provider, StringComparison.Ordinal)
        && string.Equals(left.Repository, right.Repository, StringComparison.Ordinal)
        && string.Equals(left.ArtifactId, right.ArtifactId, StringComparison.Ordinal);

    private static bool CorrelationEqual(ExternalOperationCorrelation left, ExternalOperationCorrelation right) =>
        left.DelegationId == right.DelegationId
        && left.WorkflowRun == right.WorkflowRun
        && left.StructuralNode == right.StructuralNode
        && left.NodeGeneration == right.NodeGeneration
        && string.Equals(left.ExecutionAttemptId, right.ExecutionAttemptId, StringComparison.Ordinal)
        && left.Agent == right.Agent
        && left.Task == right.Task;

    private int ConfiguredEvaluatorCallCount() =>
        (candidateValidator is null ? 0 : 1) + (candidateReviewer is null ? 0 : 1);

    private static int ActualWorkerCalls(RuntimeState runtime, DelegationExecutionSnapshot current) =>
        checked(current.Progress.WorkerCalls
            + (runtime.ResultRetrievalStarted ? 1 : 0)
            + runtime.EvaluationCallsStarted
            + (runtime.CorrectionInvocationStarted ? 1 : 0));

    private static bool IsCancellationConfirmed(RuntimeState runtime) =>
        runtime.CancellationReceipt is not null
        && (runtime.CancellationReceipt.Disposition == ExternalOperationCancellationDisposition.ConfirmedCancelled
            || (runtime.CancellationReceipt.Disposition == ExternalOperationCancellationDisposition.AlreadyTerminal
                && runtime.CancellationReceipt.State == ExternalOperationState.Cancelled));

    private async ValueTask<DelegationExecutionSnapshot> PumpStartAsync(
        RuntimeState runtime,
        DelegationExecutionSnapshot current,
        CancellationToken cancellationToken)
    {
        if (runtime.Adapter is null)
        {
            // A pending cancellation cannot be completed without an
            // executable adapter. Keep the non-terminal intent observable;
            // do not misreport it as an ordinary provider failure.
            if (IsCancellationPending(runtime))
            {
                return current;
            }

            runtime.Phase = CoordinatorPhase.Complete;
            return await PublishTerminalAsync(
                runtime,
                current,
                DelegationState.Failed,
                runtime.ProviderFailure ?? "No executable provider adapter is available.",
                [],
                cancellationToken).ConfigureAwait(false);
        }

        var pendingCancellation = runtime.CancellationRequest is not null
            || runtime.CancellationKey is not null;
        if (!pendingCancellation)
        {
            var durationExceeded = await EnforceDurationAsync(runtime, current, cancellationToken).ConfigureAwait(false);
            if (durationExceeded is not null)
            {
                return durationExceeded;
            }
        }

        if (current.Progress.State == DelegationState.Queued)
        {
            current = await PublishRunningAsync(
                runtime,
                current,
                workerCalls: 1,
                retries: 0,
                cancellationToken).ConfigureAwait(false);
        }
        else if (current.Progress.WorkerCalls >= runtime.Request.Budget.MaximumWorkerCalls
            || current.Progress.Retries >= runtime.Request.Budget.MaximumRetries)
        {
            if (pendingCancellation)
            {
                // Recovery of an accepted start is ordinary work, but a
                // cancellation intent must not be converted into an
                // unrelated Failed terminal result merely because recovery
                // has reached its ordinary budget.
                runtime.Phase = CoordinatorPhase.Start;
                return current;
            }

            runtime.Phase = CoordinatorPhase.Complete;
            return await PublishTerminalAsync(
                runtime,
                current,
                DelegationState.Failed,
                "The provider start retry budget was exhausted before a handle was captured.",
                [],
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            current = await PublishRunningAsync(
                runtime,
                current,
                workerCalls: current.Progress.WorkerCalls + 1,
                retries: current.Progress.Retries + 1,
                cancellationToken).ConfigureAwait(false);
        }

        ExternalOperationStartReceipt receipt;
        try
        {
            receipt = await runtime.Adapter.StartAsync(
                runtime.StartRequest!,
                handleRegistry,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ExternalOperationProviderException exception)
        {
            if (handleRegistry.TryGet(runtime.Correlation, out var capture) && capture is not null)
            {
                runtime.Handle = capture.Handle;
                runtime.Phase = CoordinatorPhase.Observe;
                return pendingCancellation
                    ? await CancelKnownHandleAsync(runtime, current, cancellationToken).ConfigureAwait(false)
                    : current;
            }

            if (exception.Failure.Retryable)
            {
                runtime.Phase = CoordinatorPhase.Start;
                return current;
            }

            if (pendingCancellation)
            {
                runtime.Phase = CoordinatorPhase.Start;
                return current;
            }

            runtime.Phase = CoordinatorPhase.Complete;
            return await PublishTerminalAsync(
                runtime,
                current,
                DelegationState.Failed,
                ClassifiedFailureSummary("start", exception.Failure),
                [],
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (handleRegistry.TryGet(runtime.Correlation, out var capture) && capture is not null)
            {
                runtime.Handle = capture.Handle;
                runtime.Phase = CoordinatorPhase.Observe;
                return pendingCancellation
                    ? await CancelKnownHandleAsync(runtime, current, cancellationToken).ConfigureAwait(false)
                    : current;
            }

            if (pendingCancellation)
            {
                runtime.Phase = CoordinatorPhase.Start;
                return current;
            }

            runtime.Phase = CoordinatorPhase.Complete;
            return await PublishTerminalAsync(
                runtime,
                current,
                DelegationState.Failed,
                UnclassifiedFailureSummary("start"),
                [],
                cancellationToken).ConfigureAwait(false);
        }

        try
        {
            ArgumentNullException.ThrowIfNull(receipt);
            if (receipt.Identity != runtime.StartIdentity)
            {
                throw new InvalidOperationException(
                    "The provider start receipt does not match the exact accepted start identity.");
            }

            await CaptureReturnedHandleAsync(receipt, cancellationToken).ConfigureAwait(false);
            runtime.Handle = receipt.Handle;
            runtime.Phase = CoordinatorPhase.Observe;
            return pendingCancellation
                ? await CancelKnownHandleAsync(runtime, current, cancellationToken).ConfigureAwait(false)
                : current;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            if (pendingCancellation
                && handleRegistry.TryGet(runtime.Correlation, out var recovered)
                && recovered is not null)
            {
                runtime.Handle = recovered.Handle;
                runtime.Phase = CoordinatorPhase.Observe;
                return await CancelKnownHandleAsync(runtime, current, cancellationToken).ConfigureAwait(false);
            }

            if (pendingCancellation)
            {
                runtime.Phase = CoordinatorPhase.Start;
                return current;
            }

            runtime.Phase = CoordinatorPhase.Complete;
            return await PublishTerminalAsync(
                runtime,
                current,
                DelegationState.Failed,
                "Provider start receipt validation failed.",
                [],
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<DelegationExecutionSnapshot> PumpObserveAsync(
        RuntimeState runtime,
        DelegationExecutionSnapshot current,
        CancellationToken cancellationToken)
    {
        if (runtime.Handle is null
            && handleRegistry.TryGet(runtime.Correlation, out var capture)
            && capture is not null)
        {
            runtime.Handle = capture.Handle;
        }

        if (runtime.Handle is null)
        {
            runtime.Phase = CoordinatorPhase.Start;
            return current;
        }

        var pendingCancellation = IsCancellationPending(runtime);
        var durationExceeded = pendingCancellation
            ? null
            : await EnforceDurationAsync(runtime, current, cancellationToken).ConfigureAwait(false);
        if (durationExceeded is not null)
        {
            return durationExceeded;
        }

        if (!pendingCancellation
            && current.Progress.WorkerCalls >= runtime.Request.Budget.MaximumWorkerCalls)
        {
            runtime.Phase = CoordinatorPhase.Complete;
            return await PublishTerminalAsync(
                runtime,
                current,
                DelegationState.Failed,
                "The worker-call budget was exhausted before observation.",
                [],
                cancellationToken).ConfigureAwait(false);
        }

        if (pendingCancellation && !TryReserveCancellationSafetyCall(runtime))
        {
            return await PublishCancellationNeedsSupervisorAsync(
                runtime,
                current,
                cancellationToken).ConfigureAwait(false);
        }

        ExternalOperationObservation observation;
        try
        {
            observation = await runtime.Adapter!.ObserveAsync(runtime.Handle, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ExternalOperationProviderException exception)
        {
            if (pendingCancellation)
            {
                runtime.Phase = CoordinatorPhase.Observe;
                return current;
            }

            if (exception.Failure.Retryable)
            {
                return await RetryTransportAsync(runtime, current, CoordinatorPhase.Observe,
                    ClassifiedFailureSummary("observation", exception.Failure), cancellationToken).ConfigureAwait(false);
            }

            runtime.Phase = CoordinatorPhase.Complete;
            return await PublishTerminalAsync(runtime, current, DelegationState.Failed,
                ClassifiedFailureSummary("observation", exception.Failure), [], cancellationToken,
                current.Progress.WorkerCalls + 1).ConfigureAwait(false);
        }
        catch
        {
            if (pendingCancellation)
            {
                runtime.Phase = CoordinatorPhase.Observe;
                return current;
            }

            runtime.Phase = CoordinatorPhase.Complete;
            return await PublishTerminalAsync(runtime, current, DelegationState.Failed,
                UnclassifiedFailureSummary("observation"), [], cancellationToken,
                current.Progress.WorkerCalls + 1).ConfigureAwait(false);
        }

        try
        {
            ArgumentNullException.ThrowIfNull(observation);
            if (observation.Handle != runtime.Handle)
            {
                throw new InvalidOperationException("The provider observation returned a different external handle.");
            }

            if (runtime.LastObservation is not null)
            {
                ExternalOperationObservationRules.ValidateProgression(runtime.LastObservation, observation);
            }

            runtime.LastObservation = observation;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            runtime.Phase = CoordinatorPhase.Complete;
            return await PublishTerminalAsync(
                runtime,
                current,
                DelegationState.Failed,
                "Provider observation validation failed.",
                [],
                cancellationToken,
                current.Progress.WorkerCalls + 1).ConfigureAwait(false);
        }

        if (observation.State is ExternalOperationState.Failed
            or ExternalOperationState.TimedOut
            or ExternalOperationState.Rejected
            or ExternalOperationState.Cancelled)
        {
            runtime.Phase = CoordinatorPhase.Complete;
            var state = observation.State == ExternalOperationState.Cancelled
                ? DelegationState.Cancelled
                : DelegationState.Failed;
            var summary = observation.Failure?.Summary
                ?? $"The provider reported external operation state '{observation.State}'.";
            return await PublishTerminalAsync(runtime, current, state, summary, [], cancellationToken, current.Progress.WorkerCalls + 1)
                .ConfigureAwait(false);
        }

        if (pendingCancellation)
        {
            // A rejected, unknown, or merely requested cancellation is not a
            // terminal coordinator outcome. Once observation reaches a
            // provider terminal state, however, that observed state is the
            // source of truth and must be allowed to complete normally. In
            // particular, Succeeded must proceed to result retrieval instead
            // of replaying the cancellation request forever.
            if (observation.State == ExternalOperationState.Succeeded)
            {
                runtime.CancellationReconciled = true;
                runtime.Phase = CoordinatorPhase.GetResult;
                return await PublishRunningAsync(
                    runtime,
                    current,
                    workerCalls: current.Progress.WorkerCalls + 1,
                    retries: current.Progress.Retries,
                    cancellationToken).ConfigureAwait(false);
            }

            return await CancelKnownHandleAsync(runtime, current, cancellationToken).ConfigureAwait(false);
        }

        runtime.Phase = observation.ResultAvailable || observation.State == ExternalOperationState.Succeeded
            ? CoordinatorPhase.GetResult
            : CoordinatorPhase.Observe;
        if (observation.State == ExternalOperationState.Waiting)
        {
            runtime.Phase = CoordinatorPhase.Observe;
            return await PublishWaitingAsync(
                runtime,
                current,
                observation,
                cancellationToken).ConfigureAwait(false);
        }

        return await PublishRunningAsync(
            runtime,
            current,
            workerCalls: current.Progress.WorkerCalls + 1,
            retries: current.Progress.Retries,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<DelegationExecutionSnapshot> PumpResultAsync(
        RuntimeState runtime,
        DelegationExecutionSnapshot current,
        CancellationToken cancellationToken)
    {
        if (runtime.Handle is null)
        {
            runtime.Phase = CoordinatorPhase.Start;
            return current;
        }

        var durationExceeded = await EnforceDurationAsync(runtime, current, cancellationToken).ConfigureAwait(false);
        if (durationExceeded is not null)
        {
            return durationExceeded;
        }

        if (current.Progress.WorkerCalls >= runtime.Request.Budget.MaximumWorkerCalls)
        {
            runtime.Phase = CoordinatorPhase.Complete;
            return await PublishTerminalAsync(
                runtime,
                current,
                DelegationState.Failed,
                "The worker-call budget was exhausted before result retrieval.",
                [],
                cancellationToken).ConfigureAwait(false);
        }

        ExternalOperationResult result;
        runtime.ResultRetrievalStarted = true;
        try
        {
            result = await runtime.Adapter!.GetResultAsync(runtime.Handle, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ExternalOperationProviderException exception)
        {
            if (exception.Failure.Retryable)
            {
                return await RetryTransportAsync(runtime, current, CoordinatorPhase.GetResult,
                    ClassifiedFailureSummary("result", exception.Failure), cancellationToken).ConfigureAwait(false);
            }

            runtime.Phase = CoordinatorPhase.Complete;
            return await PublishTerminalAsync(runtime, current, DelegationState.Failed,
                ClassifiedFailureSummary("result", exception.Failure), [], cancellationToken,
                current.Progress.WorkerCalls + 1).ConfigureAwait(false);
        }
        catch
        {
            runtime.Phase = CoordinatorPhase.Complete;
            return await PublishTerminalAsync(runtime, current, DelegationState.Failed,
                UnclassifiedFailureSummary("result"), [], cancellationToken,
                current.Progress.WorkerCalls + 1).ConfigureAwait(false);
        }

        try
        {
            ArgumentNullException.ThrowIfNull(result);
            if (result.Handle != runtime.Handle)
            {
                throw new InvalidOperationException("The provider result returned a different external handle.");
            }

            if (runtime.LastObservation is { State: ExternalOperationState.Succeeded }
                && result.State != ExternalOperationState.Succeeded)
            {
                throw new InvalidOperationException(
                    "A succeeded external observation cannot produce a non-succeeded result.");
            }

            if (runtime.LastObservation is not null
                && result.CompletedAt < runtime.LastObservation.ObservedAt)
            {
                throw new InvalidOperationException(
                    "An external result cannot precede its last observation timestamp.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            runtime.Phase = CoordinatorPhase.Complete;
            return await PublishTerminalAsync(
                runtime,
                current,
                DelegationState.Failed,
                "Provider result validation failed.",
                [],
                cancellationToken,
                current.Progress.WorkerCalls + 1).ConfigureAwait(false);
        }

        if (result.State == ExternalOperationState.Succeeded
            && (candidateValidator is not null || candidateReviewer is not null))
        {
            if (runtime.EvaluationCycle == 2
                && runtime.Candidate is { Revision: 2 }
                && runtime.CorrectionStarted)
            {
                // A post-cancellation reconciliation may retrieve the
                // provider's original v1 result again. Preserve the already
                // published v2 candidate and only re-enter its evaluator.
                runtime.Phase = CoordinatorPhase.Evaluate;
                return current;
            }

            if (result.Candidate is null)
            {
                runtime.Phase = CoordinatorPhase.Complete;
                return await PublishTerminalAsync(
                    runtime,
                    current,
                    DelegationState.Failed,
                    "The successful provider result did not seal a candidate revision.",
                    result.Artifacts,
                    cancellationToken,
                    current.Progress.WorkerCalls + 1).ConfigureAwait(false);
            }

            try
            {
                if (candidateRegistry is null)
                {
                    throw new InvalidOperationException("Candidate evaluation requires a publication registry.");
                }

                var publication = await candidateRegistry.PublishAsync(result.Candidate, cancellationToken)
                    .ConfigureAwait(false);
                if (!CandidateRevisionIdentity.SemanticallyEqual(publication.Candidate, result.Candidate))
                {
                    throw new InvalidOperationException("The candidate publication registry returned a different candidate content.");
                }
                runtime.Candidate = publication.Candidate;
                runtime.InitialCandidate = publication.Candidate;
                runtime.ResultArtifacts = result.Artifacts;
                runtime.ValidationInvocationId = $"validation:{runtime.DelegationId.Value:D}:1";
                runtime.ReviewInvocationId = $"review:{runtime.DelegationId.Value:D}:1";
                runtime.ImplementationInvocation = CreateImplementationInvocation(runtime, publication.Candidate, result);
                var evaluationCallCount = ConfiguredEvaluatorCallCount();
                var projectedWorkerCalls = checked(current.Progress.WorkerCalls + 1 + evaluationCallCount);
                if (projectedWorkerCalls > runtime.Request.Budget.MaximumWorkerCalls)
                {
                    runtime.Phase = CoordinatorPhase.Complete;
                    var recordedAt = Later(current.Progress.UpdatedAt, RequireNow());
                    // BudgetExceededOutcome requires consumed > limit.  The
                    // limit here is the already-authorized worker-call
                    // frontier; the configured maximum and projected demand
                    // remain explicit in the reason below.
                    var limit = BudgetQuantity.Count(current.Progress.WorkerCalls);
                    // Result retrieval has happened; evaluator calls have
                    // not.  Accounting must record actual work, never the
                    // projected demand that caused the preflight refusal.
                    var consumed = BudgetQuantity.Count(current.Progress.WorkerCalls + 1);
                    var budgetOutcome = new BudgetExceededOutcome(
                        runtime.DelegationId,
                        "qingniao-runtime-budget-v1",
                        new BudgetCharge("worker-calls", consumed),
                        limit,
                        consumed,
                        runtime.DelegationId.Value,
                        $"The evaluator call budget was insufficient for deterministic validation and independent review (required {projectedWorkerCalls}, limit {runtime.Request.Budget.MaximumWorkerCalls}).",
                        recordedAt);
                    return await PublishTerminalAsync(
                        runtime,
                        current,
                        DelegationState.BudgetExceeded,
                        $"The evaluator call budget was insufficient for deterministic validation and independent review (required {projectedWorkerCalls}, limit {runtime.Request.Budget.MaximumWorkerCalls}).",
                        result.Artifacts,
                        cancellationToken,
                        current.Progress.WorkerCalls + 1,
                        budgetOutcome).ConfigureAwait(false);
                }
                runtime.Phase = CoordinatorPhase.Evaluate;
                return current;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                runtime.Phase = CoordinatorPhase.Complete;
                return await PublishTerminalAsync(
                    runtime,
                    current,
                    DelegationState.Failed,
                    $"Candidate publication failed: {NormalizeFailure(exception.Message)}",
                    result.Artifacts,
                    cancellationToken,
                    current.Progress.WorkerCalls + 1).ConfigureAwait(false);
            }
        }

        // A candidate without evaluator branches is still sealed and exposed
        // as immutable terminal evidence when a host supplied a registry.
        if (result.State == ExternalOperationState.Succeeded && result.Candidate is not null && candidateRegistry is not null)
        {
            try
            {
                var publication = await candidateRegistry.PublishAsync(result.Candidate, cancellationToken).ConfigureAwait(false);
                if (!CandidateRevisionIdentity.SemanticallyEqual(publication.Candidate, result.Candidate))
                {
                    throw new InvalidOperationException("The candidate publication registry returned a different candidate content.");
                }

                runtime.Candidate = publication.Candidate;
                runtime.InitialCandidate = publication.Candidate;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                runtime.Phase = CoordinatorPhase.Complete;
                return await PublishTerminalAsync(
                    runtime,
                    current,
                    DelegationState.Failed,
                    $"Candidate publication failed: {NormalizeFailure(exception.Message)}",
                    result.Artifacts,
                    cancellationToken,
                    current.Progress.WorkerCalls + 1).ConfigureAwait(false);
            }
        }

        runtime.Phase = CoordinatorPhase.Complete;
        var state = result.State == ExternalOperationState.Succeeded
            ? DelegationState.Completed
            : result.State == ExternalOperationState.Cancelled
                ? DelegationState.Cancelled
                : DelegationState.Failed;
        var summary = result.Failure?.Summary ?? result.Summary;
        return await PublishTerminalAsync(
            runtime,
            current,
            state,
            summary,
            result.Artifacts,
            cancellationToken,
            current.Progress.WorkerCalls + 1).ConfigureAwait(false);
    }

    private async Task<CandidateEvaluationOutcome> EvaluateCandidateAsync(RuntimeState runtime, CancellationToken cancellationToken)
    {
        var validationTask = candidateValidator is null
            ? Task.FromResult<ValidationEvidence?>(null)
            : InvokeValidationAsync(runtime, cancellationToken);
        var reviewTask = candidateReviewer is null
            ? Task.FromResult<ReviewEvidence?>(null)
            : InvokeReviewAsync(runtime, cancellationToken);

        // Await each branch independently.  Awaiting WhenAll alone would
        // hide a successful branch when its sibling faults.
        await Task.WhenAll(validationTask, reviewTask).ConfigureAwait(false);
        return new CandidateEvaluationOutcome(
            validationTask.Result,
            reviewTask.Result,
            null,
            null);
    }

    private async Task<ValidationEvidence?> InvokeValidationAsync(RuntimeState runtime, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref runtime.EvaluationCallsStarted);
        try
        {
            var request = new CandidateValidationRequest(
                runtime.Candidate!,
                runtime.ImplementationInvocation!,
                runtime.CorrectionCorrelation ?? runtime.Correlation,
                runtime.ValidationInvocationId!);
            var evidence = await candidateValidator!.ValidateAsync(request, cancellationToken).ConfigureAwait(false);
            ValidateValidationEvidence(runtime, evidence);
            return evidence;
        }
        catch (Exception exception)
        {
            runtime.ValidationFailure = exception;
            return null;
        }
    }

    private async Task<ReviewEvidence?> InvokeReviewAsync(RuntimeState runtime, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref runtime.EvaluationCallsStarted);
        try
        {
            var request = new CandidateReviewRequest(
                runtime.Candidate!,
                runtime.ImplementationInvocation!,
                runtime.CorrectionCorrelation ?? runtime.Correlation,
                runtime.ReviewInvocationId!);
            var evidence = await candidateReviewer!.ReviewAsync(request, cancellationToken).ConfigureAwait(false);
            ValidateReviewEvidence(runtime, evidence);
            return evidence;
        }
        catch (Exception exception)
        {
            runtime.ReviewFailure = exception;
            return null;
        }
    }

    private async ValueTask<DelegationExecutionSnapshot> PublishEvaluationTerminalAsync(
        RuntimeState runtime,
        DelegationExecutionSnapshot current,
        CandidateEvaluationOutcome outcome,
        CancellationToken cancellationToken)
    {
        runtime.Phase = CoordinatorPhase.Complete;
        runtime.AggregateEvidence = new EvidenceBundle(
            runtime.Invocations,
            runtime.Validations,
            runtime.Reviews);

        var validationPass = outcome.Validation is not null && IsPass(outcome.Validation.Outcome);
        var reviewApprove = outcome.Review is not null && IsApprove(outcome.Review.Outcome);
        var concerns = new List<string>();
        if (runtime.ValidationFailure is not null)
        {
            concerns.Add(NormalizeConcern("Deterministic validation fault: " + runtime.ValidationFailure.Message));
        }
        else if (outcome.Validation is null)
        {
            concerns.Add("Deterministic validation evidence was not produced.");
        }
        else if (!validationPass)
        {
            concerns.Add("Deterministic validation failed.");
        }

        if (runtime.ReviewFailure is not null)
        {
            concerns.Add(NormalizeConcern("Independent review fault: " + runtime.ReviewFailure.Message));
        }
        else if (outcome.Review is null && candidateReviewer is not null)
        {
            concerns.Add("Independent review evidence was not produced.");
        }
        else if (outcome.Review is not null && !reviewApprove)
        {
            concerns.Add("Independent review rejected the candidate.");
        }

        var state = validationPass && reviewApprove
            ? DelegationState.Completed
            : runtime.ValidationFailure is not null || outcome.Validation is null || !validationPass
                ? DelegationState.Failed
                : DelegationState.NeedsSupervisor;
        var summary = state switch
        {
            DelegationState.Completed => "Candidate passed deterministic validation and independent review.",
            DelegationState.NeedsSupervisor => "Independent review raised a concern requiring supervision.",
            _ => "Deterministic validation did not pass.",
        };
        var evidence = new DelegationEvidence(
            [],
            [],
            validationPass ? 1 : 0,
            validationPass ? 0 : 1,
            reviewApprove,
            outcome.Review?.Findings.Count(finding => finding.Resolved) ?? 0);
        return await PublishTerminalAsync(
            runtime,
            current,
            state,
            summary,
            runtime.ResultArtifacts ?? [],
            cancellationToken,
            ActualWorkerCalls(runtime, current),
            evidence: evidence,
            unresolvedConcerns: concerns).ConfigureAwait(false);
    }

    private static void ValidateValidationEvidence(RuntimeState runtime, ValidationEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (!CandidateRevisionIdentity.SubjectEqual(runtime.Candidate, evidence.Candidate)
            || evidence.Invocation.Attempt.AttemptId != runtime.ValidationInvocationId
            || !evidence.Invocation.ExecutionCategory.StartsWith("deterministic.", StringComparison.Ordinal)
            || evidence.Invocation.Candidate is null)
        {
            throw new InvalidOperationException("Validation evidence does not identify the exact sealed candidate and invocation.");
        }
    }

    private static void ValidateReviewEvidence(RuntimeState runtime, ReviewEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (!CandidateRevisionIdentity.SubjectEqual(runtime.Candidate, evidence.Candidate)
            || evidence.Invocation.Attempt.AttemptId != runtime.ReviewInvocationId
            || evidence.Independence.ImplementationInvocationId != runtime.ImplementationInvocation!.Attempt.AttemptId
            || evidence.Independence.ReviewInvocationId != runtime.ReviewInvocationId
            || string.Equals(evidence.Independence.ImplementationInvocationId, evidence.Independence.ReviewInvocationId, StringComparison.Ordinal)
            || evidence.Invocation.Attempt.AttemptId == runtime.ImplementationInvocation!.Attempt.AttemptId
            || evidence.Invocation.Candidate is null)
        {
            throw new InvalidOperationException("Review evidence does not prove an independent attempt over the exact sealed candidate.");
        }

        // Independence claims are checked against observable invocation
        // metadata whenever it exists.  A reviewer cannot self-report a
        // different provider/profile/model while using the same values.
        var implementation = runtime.ImplementationInvocation!;
        if (evidence.Independence.Provider == IndependenceAssessment.Different
            && string.Equals(evidence.Invocation.Attempt.Provider, implementation.Attempt.Provider, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Review independence claims a different provider but both attempts use the same provider.");
        }

        if (evidence.Independence.Profile == IndependenceAssessment.Different
            && string.Equals(evidence.Invocation.Profile, implementation.Profile, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Review independence claims a different profile but invocation metadata matches.");
        }

        if (evidence.Independence.Model == IndependenceAssessment.Different
            && string.Equals(evidence.Invocation.ResolvedModel, implementation.ResolvedModel, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Review independence claims a different model but invocation metadata matches.");
        }
    }

    private static WorkerInvocationEvidence CreateImplementationInvocation(
        RuntimeState runtime,
        CandidateRevisionReference candidate,
        ExternalOperationResult result)
    {
        return new WorkerInvocationEvidence(
            runtime.DelegationId,
            runtime.Correlation.StructuralNode,
            runtime.Correlation.NodeGeneration,
            EvidenceKinds.AgentExecution,
            runtime.Handle!.ToProviderAttemptReference(),
            "succeeded",
            runtime.AcceptedAt,
            result.CompletedAt,
            AgentCapability,
            "implement",
            runtime.Correlation.Agent.Provider,
            null,
            runtime.Correlation.Agent.Identifier,
            [],
            [],
            result.Artifacts,
            candidate,
            executionCorrelation: runtime.Correlation);
    }

    private static bool IsPass(string outcome) =>
        string.Equals(outcome, "passed", StringComparison.OrdinalIgnoreCase)
        || string.Equals(outcome, "pass", StringComparison.OrdinalIgnoreCase)
        || string.Equals(outcome, "success", StringComparison.OrdinalIgnoreCase)
        || string.Equals(outcome, "approved", StringComparison.OrdinalIgnoreCase);

    private static bool IsApprove(string outcome) =>
        string.Equals(outcome, "approved", StringComparison.OrdinalIgnoreCase)
        || string.Equals(outcome, "approve", StringComparison.OrdinalIgnoreCase)
        || string.Equals(outcome, "passed", StringComparison.OrdinalIgnoreCase);

    private async ValueTask<DelegationExecutionSnapshot> RetryTransportAsync(
        RuntimeState runtime,
        DelegationExecutionSnapshot current,
        CoordinatorPhase phase,
        string summary,
        CancellationToken cancellationToken)
    {
        if (current.Progress.WorkerCalls >= runtime.Request.Budget.MaximumWorkerCalls
            || current.Progress.Retries >= runtime.Request.Budget.MaximumRetries)
        {
            runtime.Phase = CoordinatorPhase.Complete;
            return await PublishTerminalAsync(
                runtime,
                current,
                DelegationState.Failed,
                $"{summary} Retry budget exhausted.",
                [],
                cancellationToken,
                current.Progress.WorkerCalls + 1).ConfigureAwait(false);
        }

        runtime.Phase = phase;
        return await PublishRunningAsync(
            runtime,
            current,
            workerCalls: current.Progress.WorkerCalls + 1,
            retries: current.Progress.Retries + 1,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<DelegationExecutionSnapshot> CancelKnownHandleAsync(
        RuntimeState runtime,
        DelegationExecutionSnapshot current,
        CancellationToken cancellationToken)
    {
        runtime.CancellationRequest ??= new ExternalOperationCancelRequest(
            runtime.Handle!,
            runtime.CancellationKey!,
            runtime.CancellationReason!);
        if (runtime.CancellationRequest.Handle != runtime.Handle)
        {
            runtime.Phase = CoordinatorPhase.Observe;
            return current;
        }
        if (runtime.CancellationReceipt is not null)
        {
            return await ReconcileCancellationReceiptAsync(
                runtime,
                current,
                runtime.CancellationReceipt,
                cancellationToken).ConfigureAwait(false);
        }

        if (!TryReserveCancellationSafetyCall(runtime))
        {
            return await PublishCancellationNeedsSupervisorAsync(
                runtime,
                current,
                cancellationToken).ConfigureAwait(false);
        }

        runtime.CancellationIntentOnly = false;
        runtime.CancellationAttempted = true;
        current = await PublishRunningAsync(
            runtime,
            current,
            checked(current.Progress.WorkerCalls + 1),
            current.Progress.Retries,
            cancellationToken).ConfigureAwait(false);

        ExternalOperationCancellationReceipt receipt;
        try
        {
            receipt = await runtime.Adapter!.CancelAsync(
            new ExternalOperationCancelRequest(
                runtime.Handle!,
                    runtime.CancellationRequest!.CancellationKey,
                    runtime.CancellationRequest.Reason),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ExternalOperationProviderException)
        {
            // Cancellation failures are non-terminal: the coordinator must
            // continue observing the same handle. The provider exception is
            // deliberately not copied into durable result text.
            runtime.Phase = CoordinatorPhase.Observe;
            return current;
        }
        catch
        {
            runtime.Phase = CoordinatorPhase.Observe;
            return current;
        }

        try
        {
            ArgumentNullException.ThrowIfNull(receipt);
            if (receipt.Handle != runtime.Handle
                || !string.Equals(
                    receipt.CancellationKey,
                    runtime.CancellationRequest.CancellationKey,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The provider cancellation receipt did not match the exact request.");
            }

            runtime.CancellationReceipt = receipt;
        }
        catch
        {
            runtime.Phase = CoordinatorPhase.Observe;
            return current;
        }

        return await ReconcileCancellationReceiptAsync(
            runtime,
            current,
            receipt,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<DelegationExecutionSnapshot> ReconcileCancellationReceiptAsync(
        RuntimeState runtime,
        DelegationExecutionSnapshot current,
        ExternalOperationCancellationReceipt receipt,
        CancellationToken cancellationToken)
    {
        switch (receipt.Disposition)
        {
            case ExternalOperationCancellationDisposition.ConfirmedCancelled:
                runtime.Phase = CoordinatorPhase.Complete;
                return await PublishTerminalAsync(
                    runtime,
                    current,
                    DelegationState.Cancelled,
                    "The provider confirmed cancellation.",
                    [],
                    cancellationToken).ConfigureAwait(false);

            case ExternalOperationCancellationDisposition.AlreadyTerminal:
                switch (receipt.State)
                {
                    case ExternalOperationState.Cancelled:
                        runtime.Phase = CoordinatorPhase.Complete;
                        return await PublishTerminalAsync(
                            runtime,
                            current,
                            DelegationState.Cancelled,
                            "The provider reported the operation was already cancelled.",
                            [],
                            cancellationToken).ConfigureAwait(false);
                    case ExternalOperationState.Succeeded:
                        runtime.Phase = CoordinatorPhase.GetResult;
                        return current;
                    default:
                        runtime.Phase = CoordinatorPhase.Complete;
                        return await PublishTerminalAsync(
                            runtime,
                            current,
                            DelegationState.Failed,
                            "The provider reported the operation was already terminal.",
                            [],
                            cancellationToken).ConfigureAwait(false);
                }

            case ExternalOperationCancellationDisposition.Requested:
            case ExternalOperationCancellationDisposition.Rejected:
            case ExternalOperationCancellationDisposition.Unknown:
                runtime.Phase = CoordinatorPhase.Observe;
                return current;
            default:
                throw new InvalidOperationException("The provider returned an unknown cancellation disposition.");
        }
    }

    private async ValueTask<DelegationExecutionSnapshot> ResumeKnownHandleAsync(
        RuntimeState runtime,
        DelegationExecutionSnapshot current,
        CancellationToken cancellationToken)
    {
        if (runtime.ResumeReceipt is not null)
        {
            return current;
        }

        var previousHandle = runtime.ResumePreviousHandle ?? runtime.Handle!;
        ExternalOperationResumeReceipt receipt;
        try
        {
            receipt = await runtime.Adapter!.ResumeAsync(
                runtime.ResumeRequest!,
                new ResumeHandleCaptureSink(this, runtime, previousHandle),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // A lost resume response is ambiguous.  If the provider captured
            // a new handle first, that capture is durable acceptance evidence
            // and the next exact replay must observe it instead of calling
            // ResumeAsync again.  With no rotated capture, a bounded retry is
            // still allowed and consumes the normal resume budgets.
            if (runtime.ResumePreviousHandle is not null
                && runtime.Handle != runtime.ResumePreviousHandle)
            {
                runtime.ResumeAcceptedAmbiguity = true;
                runtime.ResumeAmbiguityObserved = false;
            }

            runtime.Phase = CoordinatorPhase.Observe;
            return current;
        }

        try
        {
            ArgumentNullException.ThrowIfNull(receipt);
            if (receipt.PreviousHandle != previousHandle
                || !string.Equals(receipt.ResumeKey, runtime.ResumeRequest!.ResumeKey, StringComparison.Ordinal)
                || ExternalOperationExecutionKey.Create(receipt.Handle.Correlation)
                    != ExternalOperationExecutionKey.Create(runtime.Correlation))
            {
                throw new InvalidOperationException("The provider resume receipt did not match the exact request.");
            }

            var rotated = runtime.Handle != receipt.Handle;
            if (rotated)
            {
                await handleRegistry.RotateAsync(
                    runtime.Handle!,
                    new ExternalOperationHandleCapture(receipt.Handle, receipt.AcceptedAt),
                    cancellationToken).ConfigureAwait(false);
                runtime.Handle = receipt.Handle;
            }

            runtime.ResumeReceipt = receipt;
            runtime.ResumeAcceptedAmbiguity = false;
            runtime.ResumeAmbiguityObserved = false;
            if (rotated)
            {
                runtime.LastObservation = null;
            }
        }
        catch
        {
            if (runtime.ResumePreviousHandle is not null
                && runtime.Handle != runtime.ResumePreviousHandle)
            {
                runtime.ResumeAcceptedAmbiguity = true;
                runtime.ResumeAmbiguityObserved = false;
            }

            runtime.Phase = CoordinatorPhase.Observe;
            return current;
        }

        runtime.Phase = receipt.State == ExternalOperationState.Succeeded
            ? CoordinatorPhase.GetResult
            : CoordinatorPhase.Observe;
        if (receipt.State is ExternalOperationState.Cancelled
            or ExternalOperationState.Failed
            or ExternalOperationState.TimedOut
            or ExternalOperationState.Rejected)
        {
            runtime.Phase = CoordinatorPhase.Complete;
            return await PublishTerminalAsync(
                runtime,
                current,
                receipt.State == ExternalOperationState.Cancelled
                    ? DelegationState.Cancelled
                    : DelegationState.Failed,
                receipt.State == ExternalOperationState.Cancelled
                    ? "The provider reported the resumed operation was cancelled."
                    : "The provider reported the resumed operation was terminal.",
                [],
                cancellationToken).ConfigureAwait(false);
        }

        return current;
    }

    private async ValueTask<DelegationExecutionSnapshot> PublishRunningAsync(
        RuntimeState runtime,
        DelegationExecutionSnapshot current,
        int workerCalls,
        int retries,
        CancellationToken cancellationToken)
    {
        var timestamp = Later(current.Progress.UpdatedAt, RequireNow());
        var progress = new DelegationProgress(
            runtime.DelegationId,
            DelegationState.Running,
            checked(current.Progress.Revision + 1),
            ["implement"],
            current.Progress.CompletedSteps,
            workerCalls,
            retries,
            timestamp);
        var published = await executionStore.PublishProgressAsync(
            runtime.DelegationId,
            progress,
            cancellationToken).ConfigureAwait(false);
        if (current.Progress.State == DelegationState.WaitingForSupervisor)
        {
            // Any accepted transition out of a wait closes that checkpoint
            // episode. The next provider Waiting observation gets a fresh ID.
            runtime.WakeHint = null;
            runtime.CheckpointId = null;
        }

        return published;
    }

    private async ValueTask<DelegationExecutionSnapshot> PublishWaitingAsync(
        RuntimeState runtime,
        DelegationExecutionSnapshot current,
        ExternalOperationObservation observation,
        CancellationToken cancellationToken)
    {
        var timestamp = Later(current.Progress.UpdatedAt, RequireNow());
        if (runtime.CheckpointId is null)
        {
            // A new provider Waiting episode gets a fresh supervisory
            // decision and resume application state. The external handle and
            // its correlation deliberately remain unchanged: this is not a
            // semantic node re-execution or a new NodeGeneration.
            runtime.ResumeRequest = null;
            runtime.ResumeReceipt = null;
            runtime.ResumeAttempted = false;
            runtime.ResumeAcceptedAmbiguity = false;
            runtime.ResumeAmbiguityObserved = false;
            runtime.ResumePreviousHandle = null;
            runtime.AcceptedIntervention = null;
        }

        var checkpointId = runtime.CheckpointId ??= new SupervisorCheckpointId(Guid.NewGuid());
        // DelegationRequest intentionally has no Hongxian session field. This
        // deterministic placeholder is only an in-memory correlation value;
        // it is not a durable or authoritative Hongxian session identity.
        var session = new HongxianSessionReference(
            $"qingniao-inmemory-session:{runtime.DelegationId.Value:D}");
        var checkpoint = new SupervisorCheckpointDescriptor(
            checkpointId,
            session,
            runtime.DelegationId,
            runtime.Resolution.PlanRevision,
            runtime.Correlation.WorkflowRun,
            runtime.Correlation.StructuralNode,
            new NodeGenerationId(runtime.Correlation.NodeGeneration.Value),
            checked(current.Progress.Revision + 1),
            dependentProgressGated: true);
        var progress = new DelegationProgress(
            runtime.DelegationId,
            DelegationState.WaitingForSupervisor,
            checked(current.Progress.Revision + 1),
            ["implement"],
            current.Progress.CompletedSteps,
            checked(current.Progress.WorkerCalls + 1),
            current.Progress.Retries,
            timestamp,
            checkpoint);
        var published = await executionStore.PublishProgressAsync(
            runtime.DelegationId,
            progress,
            cancellationToken).ConfigureAwait(false);

        // Publication precedes both the wake hint and any later host
        // activation, so observers never receive an unusable fence.
        runtime.WakeHint = new WakeHint(
            runtime.DelegationId,
            checkpointId,
            observation.ProviderStatus ?? "The external operation is waiting for supervisor input.",
            published.Progress.Revision,
            expiresAt: published.Progress.UpdatedAt.AddMinutes(30));
        return published;
    }

    private async ValueTask<DelegationExecutionSnapshot> PublishTerminalAsync(
        RuntimeState runtime,
        DelegationExecutionSnapshot current,
        DelegationState state,
        string summary,
        IReadOnlyList<DelegationArtifactReference> artifacts,
        CancellationToken cancellationToken,
        int? workerCalls = null,
        BudgetExceededOutcome? budgetExceeded = null,
        DelegationEvidence? evidence = null,
        IReadOnlyList<string>? unresolvedConcerns = null)
    {
        var timestamp = Later(current.Progress.UpdatedAt, RequireNow());
        runtime.AggregateEvidence = new EvidenceBundle(
            runtime.Invocations,
            runtime.Validations,
            runtime.Reviews);
        var progress = new DelegationProgress(
            runtime.DelegationId,
            state,
            checked(current.Progress.Revision + 1),
            [],
            ["implement"],
            workerCalls ?? ActualWorkerCalls(runtime, current),
            current.Progress.Retries,
            timestamp);
        var normalizedEvidence = runtime.AggregateEvidence;
        DelegationResultReference? resultReference = null;
        if (runtime.Candidate is not null)
        {
            resultReference = new DelegationResultReference(
                runtime.DelegationId,
                new DelegationResultId(runtime.DelegationId.Value),
                runtime.Candidate,
                artifacts,
                normalizedEvidence);
        }
        var result = new DelegationResult(
            runtime.DelegationId,
            state,
            NormalizeFailure(summary),
            evidence ?? new DelegationEvidence([], [], 0, 0, null, 0),
            artifacts,
            unresolvedConcerns ?? (state == DelegationState.Completed ? [] : [NormalizeFailure(summary)]),
            timestamp,
            normalizedEvidence: normalizedEvidence,
            budgetExceeded: budgetExceeded,
            resultReference: resultReference);
        var published = await executionStore.PublishTerminalAsync(
            runtime.DelegationId,
            progress,
            result,
            cancellationToken).ConfigureAwait(false);
        runtime.WakeHint = null;
        runtime.CheckpointId = null;
        return published;
    }

    private ValueTask<DelegationExecutionSnapshot> PublishCancellationNeedsSupervisorAsync(
        RuntimeState runtime,
        DelegationExecutionSnapshot current,
        CancellationToken cancellationToken) =>
        PublishTerminalAsync(
            runtime,
            current,
            DelegationState.NeedsSupervisor,
            "Cancellation remains unresolved after the coordinator safety observation ceiling.",
            [],
            cancellationToken);

    private async ValueTask CaptureReturnedHandleAsync(
        ExternalOperationStartReceipt receipt,
        CancellationToken cancellationToken)
    {
        await handleRegistry.CaptureAsync(
            new ExternalOperationHandleCapture(receipt.Handle, receipt.AcceptedAt),
            cancellationToken).ConfigureAwait(false);
    }

    private RuntimeState GetRuntime(DelegationId delegationId)
    {
        lock (stateGate)
        {
            return states.TryGetValue(delegationId, out var runtime)
                ? runtime
                : throw new DelegationCoordinatorStateUnavailableException(delegationId);
        }
    }

    private ExternalOperationHandle ResolveResumeHandleForRequest(
        DelegationId delegationId,
        string resumeKey)
    {
        var runtime = GetRuntime(delegationId);
        if (runtime.ResumeRequest is not null
            && string.Equals(runtime.ResumeRequest.ResumeKey, resumeKey, StringComparison.Ordinal))
        {
            return runtime.ResumeRequest.Handle;
        }

        return runtime.Handle
            ?? throw new InvalidOperationException(
                "A resume request requires a known captured external handle.");
    }

    private static void EnsureExpectedRevision(
        DelegationId delegationId,
        DelegationExecutionSnapshot current,
        long expectedRevision)
    {
        if (current.Progress.Revision != expectedRevision)
        {
            throw new DelegationExecutionStaleException(
                delegationId,
                current.Progress.Revision,
                expectedRevision);
        }
    }

    private static bool CancellationRequestsEqual(
        ExternalOperationCancelRequest left,
        ExternalOperationCancelRequest right) =>
        left.Handle == right.Handle
        && string.Equals(left.CancellationKey, right.CancellationKey, StringComparison.Ordinal)
        && string.Equals(left.Reason, right.Reason, StringComparison.Ordinal);

    private static bool ResumeRequestsEqual(
        ExternalOperationResumeRequest left,
        ExternalOperationResumeRequest right) =>
        left.Handle == right.Handle
        && string.Equals(left.ResumeKey, right.ResumeKey, StringComparison.Ordinal)
        && string.Equals(left.Reason, right.Reason, StringComparison.Ordinal)
        && left.CorrectionArtifacts.SequenceEqual(right.CorrectionArtifacts);

    private static bool HandleMatchesRuntime(RuntimeState runtime, ExternalOperationHandle handle)
    {
        try
        {
            var requestKey = ExternalOperationExecutionKey.Create(handle.Correlation);
            var runtimeKey = ExternalOperationExecutionKey.Create(runtime.Correlation);
            return requestKey == runtimeKey
                && string.Equals(handle.Provider, runtime.Correlation.Agent.Provider, StringComparison.Ordinal)
                && string.Equals(handle.ProtocolVersion, runtime.Correlation.Agent.ProtocolVersion, StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsCancellationPending(RuntimeState runtime) =>
        runtime.CancellationKey is not null;

    private static bool TryReserveCancellationSafetyCall(RuntimeState runtime)
    {
        if (runtime.CancellationSafetyCalls >= CancellationSafetyCallLimit)
        {
            return false;
        }

        runtime.CancellationSafetyCalls++;
        return true;
    }

    private DateTimeOffset RequireNow()
    {
        var value = now();
        return value == default
            ? throw new InvalidOperationException("The coordinator clock returned a default timestamp.")
            : value;
    }

    private static DateTimeOffset Later(DateTimeOffset left, DateTimeOffset right) =>
        left >= right ? left : right;

    private async ValueTask<DelegationExecutionSnapshot?> EnforceDurationAsync(
        RuntimeState runtime,
        DelegationExecutionSnapshot current,
        CancellationToken cancellationToken)
    {
        var maximum = runtime.Request.Budget.MaximumDuration;
        if (maximum is null)
        {
            return null;
        }

        var currentTime = RequireNow();
        var elapsed = currentTime - runtime.AcceptedAt;
        if (elapsed < maximum.Value)
        {
            return null;
        }

        runtime.Phase = CoordinatorPhase.Complete;
        var recordedAt = Later(current.Progress.UpdatedAt, currentTime);
        var limit = BudgetQuantity.Ticks(maximum.Value.Ticks);
        var consumed = BudgetQuantity.Ticks(Math.Max(maximum.Value.Ticks + 1, elapsed.Ticks));
        var outcome = new BudgetExceededOutcome(
            runtime.DelegationId,
            "qingniao-runtime-budget-v1",
            new BudgetCharge("duration", consumed),
            limit,
            consumed,
            runtime.DelegationId.Value,
            "Maximum delegation duration exceeded.",
            recordedAt);
        return await PublishTerminalAsync(
            runtime,
            current,
            DelegationState.BudgetExceeded,
            "Maximum delegation duration exceeded.",
            [],
            cancellationToken,
            current.Progress.WorkerCalls,
            outcome).ConfigureAwait(false);
    }

    private static string ClassifiedFailureSummary(string operation, ExternalOperationFailure failure) =>
        $"Provider {operation} reported classified failure code '{failure.Code}'.";

    private static string UnclassifiedFailureSummary(string operation) =>
        $"Provider {operation} failed with an unclassified error.";

    private static string NormalizeFailure(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? "The provider operation failed."
            : value.Trim().Length <= 16_384
                ? value.Trim()
                : value.Trim()[..16_384];

    private static string NormalizeConcern(string value)
    {
        var normalized = NormalizeFailure(value);
        const int maximumConcernLength = 4_096;
        return normalized.Length <= maximumConcernLength
            ? normalized
            : normalized[..maximumConcernLength];
    }

    private static bool InterventionsEqual(SupervisorIntervention left, SupervisorIntervention right) =>
        left.DelegationId == right.DelegationId
        && left.CheckpointId == right.CheckpointId
        && string.Equals(left.InterventionKey, right.InterventionKey, StringComparison.Ordinal)
        && left.ExpectedRevision == right.ExpectedRevision
        && SupervisorInterventionIdentity.Compute(left) == SupervisorInterventionIdentity.Compute(right);

    private enum CoordinatorPhase
    {
        Start,
        Observe,
        GetResult,
        Evaluate,
        Complete,
    }

    private sealed class ResumeHandleCaptureSink(
        InMemoryDelegationCoordinator owner,
        RuntimeState runtime,
        ExternalOperationHandle expectedHandle) : IExternalOperationHandleCaptureSink
    {
        public ValueTask CaptureAsync(
            ExternalOperationHandleCapture capture,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(capture);
            return owner.CaptureResumedHandleAsync(runtime, expectedHandle, capture, cancellationToken);
        }
    }

    private async ValueTask CaptureResumedHandleAsync(
        RuntimeState runtime,
        ExternalOperationHandle expectedHandle,
        ExternalOperationHandleCapture capture,
        CancellationToken cancellationToken)
    {
        // The request's previous handle is immutable.  A provider callback
        // may arrive after another callback has already rotated the current
        // handle, but it still must prove the original binding.
        if (runtime.ResumePreviousHandle is null
            || runtime.ResumePreviousHandle != expectedHandle)
        {
            throw new InvalidOperationException(
                "A resumed handle capture must match the immutable previous handle.");
        }

        var rotated = runtime.Handle != capture.Handle;
        await handleRegistry.RotateAsync(expectedHandle, capture, cancellationToken).ConfigureAwait(false);
        runtime.Handle = capture.Handle;
        if (rotated)
        {
            runtime.LastObservation = null;
            runtime.ResumeAcceptedAmbiguity = true;
            runtime.ResumeAmbiguityObserved = false;
        }
    }

    private sealed class RuntimeState
    {
        internal RuntimeState(
            DelegationId delegationId,
            DelegationRequest request,
            WorkflowPlanResolution resolution,
            ProviderRegistrySnapshot providerSnapshot,
            ProviderMatch? match,
            IExternalOperationProvider? adapter,
            string? providerFailure,
            SimingExternalOperationSemanticFingerprintVerifier fingerprintVerifier,
            DateTimeOffset acceptedAt)
        {
            DelegationId = delegationId;
            Request = request;
            Resolution = resolution;
            ProviderSnapshot = providerSnapshot;
            Match = match;
            Adapter = adapter;
            ProviderFailure = providerFailure;
            AcceptedAt = acceptedAt == default
                ? throw new ArgumentException("The accepted timestamp is required.", nameof(acceptedAt))
                : acceptedAt;
            Phase = CoordinatorPhase.Start;

            if (match is not null)
            {
                var agent = new ExternalAgentReference(
                    match.Provider.Provider,
                    match.Provider.Provider,
                    AgentProtocolVersion);
                Correlation = new ExternalOperationCorrelation(
                    delegationId,
                    new WorkflowRunExecutionReference(
                        "qingniao-inmemory",
                        delegationId.Value.ToString("N"),
                        "epoch-1"),
                    new StructuralNodeReference("implement"),
                    new NodeGenerationId(delegationId.Value),
                    "attempt-1",
                    agent);
                var semanticInput = new ExternalOperationSemanticInputEnvelope(
                    delegationId,
                    agent,
                    AgentCapability,
                    [],
                    null,
                    null);
                StartIdentity = new ExternalOperationStartIdentity(
                    delegationId,
                    Correlation.WorkflowRun,
                    Correlation.StructuralNode,
                    Correlation.NodeGeneration,
                    Correlation.ExecutionAttemptId,
                    $"qingniao:{delegationId.Value:D}:implement:attempt-1",
                    fingerprintVerifier.Compute(semanticInput));
                StartRequest = new ExternalOperationStartRequest(
                    StartIdentity,
                    Correlation,
                    AgentCapability,
                    []);
                StartRequest.VerifySemanticFingerprint(fingerprintVerifier);
            }
            else
            {
                Correlation = null!;
            }
        }

        internal DelegationId DelegationId { get; }
        internal DelegationRequest Request { get; }
        internal WorkflowPlanResolution Resolution { get; }
        internal ProviderRegistrySnapshot ProviderSnapshot { get; }
        internal ProviderMatch? Match { get; }
        internal IExternalOperationProvider? Adapter { get; }
        internal string? ProviderFailure { get; }
        internal DateTimeOffset AcceptedAt { get; }
        internal SemaphoreSlim Gate { get; } = new(1, 1);
        internal CoordinatorPhase Phase { get; set; }
        internal ExternalOperationCorrelation Correlation { get; }
        internal ExternalOperationStartIdentity? StartIdentity { get; }
        internal ExternalOperationStartRequest? StartRequest { get; }
        internal ExternalOperationHandle? Handle { get; set; }
        internal ExternalOperationObservation? LastObservation { get; set; }
        internal SupervisorCheckpointId? CheckpointId { get; set; }
        internal WakeHint? WakeHint { get; set; }
        internal SupervisorIntervention? AcceptedIntervention { get; set; }
        internal ExternalOperationCancelRequest? CancellationRequest { get; set; }
        internal string? CancellationKey { get; set; }
        internal string? CancellationReason { get; set; }
        internal ExternalOperationCancellationReceipt? CancellationReceipt { get; set; }
        internal bool CancellationIntentOnly { get; set; }
        internal bool CancellationAttempted { get; set; }
        internal int CancellationSafetyCalls { get; set; }
        internal ExternalOperationResumeRequest? ResumeRequest { get; set; }
        internal ExternalOperationResumeReceipt? ResumeReceipt { get; set; }
        internal ExternalOperationHandle? ResumePreviousHandle { get; set; }
        internal bool ResumeAttempted { get; set; }
        internal bool ResumeAcceptedAmbiguity { get; set; }
        internal bool ResumeAmbiguityObserved { get; set; }
        internal CandidateRevisionReference? Candidate { get; set; }
        internal CandidateRevisionReference? InitialCandidate { get; set; }
        internal IReadOnlyList<DelegationArtifactReference>? ResultArtifacts { get; set; }
        internal WorkerInvocationEvidence? ImplementationInvocation { get; set; }
        internal EvidenceBundle? AggregateEvidence { get; set; }
        internal List<WorkerInvocationEvidence> Invocations { get; } = [];
        internal List<ValidationEvidence> Validations { get; } = [];
        internal List<ReviewEvidence> Reviews { get; } = [];
        internal Task<CandidateEvaluationOutcome>? EvaluationTask { get; set; }
        internal CancellationTokenSource? EvaluationCancellation { get; set; }
        internal object EvaluationSync { get; } = new();
        internal Task<CandidateCorrectionOutcome>? CorrectionTask { get; set; }
        internal CancellationTokenSource? CorrectionCancellation { get; set; }
        internal bool CorrectionStarted { get; set; }
        internal bool CorrectionInvocationStarted { get; set; }
        internal bool ResultRetrievalStarted { get; set; }
        internal bool CancellationReconciled { get; set; }
        internal int EvaluationCallsStarted;
        internal int EvaluationCycle { get; set; } = 1;
        internal NodeGenerationId? CorrectionGeneration { get; set; }
        internal ExternalOperationCorrelation? CorrectionCorrelation { get; set; }
        internal Exception? ValidationFailure { get; set; }
        internal Exception? ReviewFailure { get; set; }
        internal string? ValidationInvocationId { get; set; }
        internal string? ReviewInvocationId { get; set; }
    }
}
