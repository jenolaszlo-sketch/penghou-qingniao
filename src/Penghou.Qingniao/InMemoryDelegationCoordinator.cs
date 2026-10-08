namespace Penghou.Qingniao;

/// <summary>Raised when an in-memory coordinator cannot recover its private runtime state.</summary>
internal sealed class DelegationCoordinatorStateUnavailableException : InvalidOperationException
{
    internal DelegationCoordinatorStateUnavailableException(DelegationId delegationId)
        : base($"Delegation '{delegationId}' has no in-memory coordinator state.") => DelegationId = delegationId;

    internal DelegationId DelegationId { get; }
}

/// <summary>
/// A deterministic in-memory coordinator for one bounded delegated execution.
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
    private readonly IDelegationAdmissionVerifier? admissionVerifier;
    private readonly IProviderRegistry providerRegistry;
    private readonly InMemoryExternalOperationProviderCatalog adapterCatalog;
    private readonly InMemoryExternalOperationHandleCaptureRegistry handleRegistry;
    private readonly IExternalOperationHandleCaptureSink providerCaptureSink;
    private readonly InMemoryDelegationExecutionStore executionStore;
    private readonly DelegationExecutionPublisher publisher;
    private readonly IExternalOperationSemanticFingerprintVerifier fingerprintVerifier;
    private readonly ISupervisorContextProvider? contextProvider;
    private readonly ISupervisorInterventionAcceptanceRegistry interventionRegistry;
    private readonly ICandidateRevisionPublicationRegistry? candidateRegistry;
    private readonly IDeterministicCandidateValidator? candidateValidator;
    private readonly IIndependentCandidateReviewer? candidateReviewer;
    private readonly ICandidateCorrector? candidateCorrector;
    private readonly ICandidateVerificationPolicy verificationPolicy;
    private readonly IDelegationInputMaterializer? inputMaterializer;
    private readonly IDelegationAuthorityPreflight? authorityPreflight;
    private readonly CandidateEvaluationRunner evaluationRunner;
    private readonly Func<DateTimeOffset> now;

    internal InMemoryDelegationCoordinator(
        IDelegationAcceptanceRegistry acceptanceRegistry,
        IDelegationAdmissionVerifier? admissionVerifier,
        IProviderRegistry providerRegistry,
        InMemoryExternalOperationProviderCatalog adapterCatalog,
        IExternalOperationSemanticFingerprintVerifier? fingerprintVerifier = null,
        InMemoryExternalOperationHandleCaptureRegistry? handleRegistry = null,
        InMemoryDelegationExecutionStore? executionStore = null,
        Func<DateTimeOffset>? now = null,
        ISupervisorContextProvider? contextProvider = null,
        ISupervisorInterventionAcceptanceRegistry? interventionRegistry = null,
        ICandidateRevisionPublicationRegistry? candidateRegistry = null,
        IDeterministicCandidateValidator? candidateValidator = null,
        IIndependentCandidateReviewer? candidateReviewer = null,
        ICandidateCorrector? candidateCorrector = null,
        ICandidateVerificationPolicy? verificationPolicy = null,
        IDelegationInputMaterializer? inputMaterializer = null,
        IExternalOperationHandleCaptureSink? durableHandleWitness = null,
        IDelegationAuthorityPreflight? authorityPreflight = null)
    {
        this.acceptanceRegistry = acceptanceRegistry ?? throw new ArgumentNullException(nameof(acceptanceRegistry));
        this.admissionVerifier = admissionVerifier;
        this.providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));
        this.adapterCatalog = adapterCatalog ?? throw new ArgumentNullException(nameof(adapterCatalog));
        this.fingerprintVerifier = fingerprintVerifier ?? new LocalSemanticFingerprintVerifier();
        this.handleRegistry = handleRegistry ?? new InMemoryExternalOperationHandleCaptureRegistry();
        this.providerCaptureSink = durableHandleWitness is null
            ? this.handleRegistry
            : new WitnessingHandleCaptureSink(this.handleRegistry, durableHandleWitness);
        this.executionStore = executionStore ?? new InMemoryDelegationExecutionStore();
        this.now = now ?? (() => DateTimeOffset.UtcNow);
        this.publisher = new DelegationExecutionPublisher(this.executionStore, this.now);
        this.contextProvider = contextProvider;
        this.interventionRegistry = interventionRegistry ?? new InMemorySupervisorInterventionAcceptanceRegistry();
        this.candidateRegistry = candidateRegistry ?? new InMemoryCandidateRevisionPublicationRegistry();
        this.candidateValidator = candidateValidator;
        this.candidateReviewer = candidateReviewer;
        this.candidateCorrector = candidateCorrector;
        if (verificationPolicy is null && (candidateValidator is not null || candidateReviewer is not null))
        {
            // Fail fast: evaluators without a host decision policy can only
            // ever reject, which is a confusing wiring bug, not a mode.
            throw new ArgumentException(
                "A verification policy is required when candidate evaluators are configured.",
                nameof(verificationPolicy));
        }

        this.verificationPolicy = verificationPolicy ?? FailClosedVerificationPolicy.Instance;
        this.inputMaterializer = inputMaterializer;
        this.authorityPreflight = authorityPreflight;
        if (this.verificationPolicy.MaxVerificationRounds < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(verificationPolicy),
                "A verification policy must allow at least one verification round.");
        }

        this.evaluationRunner = new CandidateEvaluationRunner(
            this.publisher,
            this.executionStore,
            this.candidateRegistry,
            this.candidateValidator,
            this.candidateReviewer,
            this.candidateCorrector,
            this.verificationPolicy);
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

        if (admissionVerifier is not null)
        {
            var decision = admissionVerifier.Verify(new DelegationAdmissionContext(caller, request))
                ?? throw new DelegationAdmissionException(
                    "The host admission verifier returned no decision.",
                    DelegationAdmissionStatus.Unknown,
                    "The host admission verifier returned no decision.");
            if (!decision.IsAdmitted)
            {
                throw new DelegationAdmissionException(
                    $"The host rejected the delegation{(decision.Reason is null ? "." : $": {decision.Reason}")}",
                    decision.Status,
                    decision.Reason);
            }
        }

        var acceptance = await acceptanceRegistry.AcceptAsync(
            caller,
            request,
            cancellationToken).ConfigureAwait(false);

        lock (stateGate)
        {
            if (!acceptance.IsNew && states.ContainsKey(acceptance.DelegationId))
            {
                return acceptance;
            }
        }

        if (!acceptance.IsNew)
        {
            // A replayed acceptance without live runtime state is only safe to
            // complete when no execution record exists either: provider contact
            // is impossible without runtime state, so rebuilding is virgin
            // initialization rather than a relaunch. Any retained execution
            // record means unknown history (a previous generation may have run
            // elsewhere), which this in-memory runtime refuses to silently
            // relaunch or take over. Durable recovery across restarts belongs
            // to the Zhinu-backed host integration, not to this store.
            try
            {
                await executionStore.GetAsync(acceptance.DelegationId, cancellationToken).ConfigureAwait(false);
            }
            catch (DelegationExecutionNotFoundException)
            {
                return await InitializeRuntimeAsync(acceptance, request, cancellationToken).ConfigureAwait(false);
            }

            throw new DelegationCoordinatorStateUnavailableException(acceptance.DelegationId);
        }

        return await InitializeRuntimeAsync(acceptance, request, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<DelegationAcceptance> InitializeRuntimeAsync(
        DelegationAcceptance acceptance,
        DelegationRequest request,
        CancellationToken cancellationToken)
    {
        // The caller supplies the provider identity on the request. Qingniao
        // resolves that exact provider, verifies registration, availability,
        // and required capabilities, and rejects anything else. It never
        // chooses between providers.
        var providerResolution = providerRegistry.Resolve(
            request.Provider,
            request.RequiredCapabilities);
        IExternalOperationProvider? adapter = null;
        string? providerFailure = null;
        if (!providerResolution.IsResolved)
        {
            providerFailure = providerResolution.Status switch
            {
                ProviderResolutionStatus.UnknownProvider =>
                    $"No provider is registered for identity '{providerResolution.Provider}'.",
                ProviderResolutionStatus.DisabledProvider =>
                    $"Provider '{providerResolution.Provider}' is registered but disabled.",
                ProviderResolutionStatus.IncompatibleProvider =>
                    $"Provider '{providerResolution.Provider}' does not satisfy required capabilities: {string.Join(", ", providerResolution.MissingCapabilities)}.",
                _ => $"Provider '{providerResolution.Provider}' could not be resolved.",
            };
        }
        else
        {
            var lookup = adapterCatalog.Lookup(providerResolution.Descriptor!);
            if (!lookup.IsFound)
            {
                providerFailure = lookup.Status == ProviderAdapterLookupStatus.Unauthorized
                    ? $"Provider '{providerResolution.Provider}' is not authorized for execution."
                    : $"Provider '{providerResolution.Provider}' has no executable adapter.";
            }
            else
            {
                adapter = lookup.Adapter;
            }
        }

        MaterializedDelegationInputs? materialized = null;
        if (adapter is not null)
        {
            // Host materialization turns the admitted objective, workspace,
            // criteria and context into immutable provider inputs bound into
            // the start identity below. It runs on every initialization
            // (including virgin replays), so it must be deterministic and
            // side-effect free; provider contact happens later in the pump,
            // never here. Without a host materializer the start carries the
            // legacy unmaterialized shape: empty inputs, no bounds, and a
            // provider-name-derived agent record.
            materialized = inputMaterializer?.Materialize(request, acceptance.DelegationId);
        }

        DelegationExecutionSnapshot? existing = null;
        try
        {
            existing = await executionStore.GetAsync(
                acceptance.DelegationId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (DelegationExecutionNotFoundException)
        {
        }

        DateTimeOffset acceptedAt;
        if (existing is not null)
        {
            if (existing.Progress.State != DelegationState.Queued || existing.Result is not null)
            {
                throw new DelegationCoordinatorStateUnavailableException(acceptance.DelegationId);
            }

            acceptedAt = existing.Progress.UpdatedAt;
        }
        else
        {
            acceptedAt = RequireNow();
        }

        var runtime = new RuntimeState(
            acceptance.DelegationId,
            request,
            providerResolution.Descriptor,
            adapter,
            providerFailure,
            fingerprintVerifier,
            acceptedAt,
            materialized);

        // The authority start boundary: after DelegationId and Generation exist
        // and before any durable startable record is committed. Invoked only for
        // delegations that request derived authority and only when an adapter
        // will actually start; a delegation with no request and no adapter keeps
        // its existing behavior. A replayed record already carries its
        // attachment; the preflight for the same generation is idempotent.
        var executionAttachment = existing?.ExecutionAttachment;
        if (adapter is not null
            && (request.ParentGrantId is not null || request.RequestedAuthority is not null))
        {
            executionAttachment = await PreflightAuthorityAsync(runtime, request, cancellationToken).ConfigureAwait(false);
        }

        runtime.ExecutionAttachment = executionAttachment;
        if (runtime.StartRequest is not null && executionAttachment is not null)
        {
            runtime.StartRequest = runtime.StartRequest with { ExecutionAttachment = executionAttachment };
        }

        if (existing is null)
        {
            await executionStore.CreateAsync(
                acceptance.DelegationId,
                acceptedAt,
                executionAttachment,
                cancellationToken).ConfigureAwait(false);
        }

        lock (stateGate)
        {
            states.TryAdd(acceptance.DelegationId, runtime);
        }

        return acceptance;
    }

    /// <summary>
    /// Runs the host authority preflight for one generation. Deny and
    /// Unavailable both fail closed: the generation must not start, and neither
    /// outcome degrades to parent authority, no authority, default admit, or
    /// provider capability.
    /// </summary>
    private async ValueTask<DelegationExecutionAttachment> PreflightAuthorityAsync(
        RuntimeState runtime,
        DelegationRequest request,
        CancellationToken cancellationToken)
    {
        if (authorityPreflight is null)
        {
            throw new DelegationAuthorityPreflightException(
                DelegationAuthorityPreflightStatus.Unavailable,
                "The delegation requests derived authority but no host authority preflight is configured.");
        }

        if (request.ParentGrantId is null || request.RequestedAuthority is null)
        {
            throw new DelegationAuthorityPreflightException(
                DelegationAuthorityPreflightStatus.Deny,
                "A derived-authority delegation requires both a parent grant and a requested authority.");
        }

        var context = new DelegationAuthorityPreflightContext(
            runtime.DelegationId,
            runtime.Generation,
            request.ParentGrantId,
            request.RequestedAuthority);

        var decision = await authorityPreflight.PreflightAsync(context, cancellationToken).ConfigureAwait(false)
            ?? throw new DelegationAuthorityPreflightException(
                DelegationAuthorityPreflightStatus.Unavailable,
                "The host authority preflight returned no decision.");

        if (!decision.IsPermit)
        {
            throw new DelegationAuthorityPreflightException(
                decision.Status,
                decision.Reason ?? "The host authority preflight did not permit the generation.");
        }

        return decision.ExecutionAttachment!;
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
                return await publisher.PublishTerminalAsync(
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
                return await publisher.PublishTerminalAsync(
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
            return await publisher.PublishTerminalAsync(runtime, current, DelegationState.Failed,
                "The provider resume retry budget was exhausted.", [], cancellationToken).ConfigureAwait(false);
        }

        runtime.ResumeAttempted = true;
        current = await publisher.PublishRunningAsync(runtime, current,
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
                return await evaluationRunner.RunEvaluationAndCorrectionAsync(runtime, delegationId).ConfigureAwait(false);
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

    internal static bool IsCancellationConfirmed(RuntimeState runtime) =>
        runtime.CancellationReceipt is not null
        && (runtime.CancellationReceipt.Disposition == ExternalOperationCancellationDisposition.ConfirmedCancelled
            || (runtime.CancellationReceipt.Disposition == ExternalOperationCancellationDisposition.AlreadyTerminal
                && runtime.CancellationReceipt.State == ExternalOperationState.Cancelled));

    /// <summary>
    /// Runs one provider call without holding the per-runtime gate. The caller
    /// reserves attempt and revision state under the gate first (worker-call
    /// accounting, the in-flight marker, cancellation intent); the gate stays
    /// free while provider I/O is pending so cancellation intent and duration
    /// checks remain observable, then the caller reconciles the outcome under
    /// the same fence. Provider work is durable: the caller's transport token
    /// never cancels it, so caller-wait cancellation, operation deadlines, and
    /// cancellation of externally accepted work stay three separate
    /// mechanisms. Re-acquisition cannot deadlock: no path holds this gate
    /// across the call, and providers must not call back into the coordinator.
    /// </summary>
    private async ValueTask<T> CallProviderOutsideGateAsync<T>(
        RuntimeState runtime,
        Func<CancellationToken, ValueTask<T>> call)
    {
        System.Diagnostics.Debug.Assert(runtime.Gate.CurrentCount == 0, "Provider calls require the runtime gate.");
        runtime.Gate.Release();
        try
        {
            return await call(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await runtime.Gate.WaitAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Re-reads the execution store after provider I/O. A terminal store wins
    /// outright and any revision movement means another pump already advanced
    /// the delegation, so the late provider outcome is stale. Returns the
    /// superseding snapshot, or null when the caller may proceed.
    /// </summary>
    private async ValueTask<DelegationExecutionSnapshot?> ReconcileProviderReturnAsync(
        DelegationId delegationId,
        long reservedRevision,
        CancellationToken cancellationToken)
    {
        var fresh = await executionStore.GetAsync(delegationId, cancellationToken).ConfigureAwait(false);
        if (DelegationLifecycle.IsTerminal(fresh.Progress.State)
            || fresh.Progress.Revision != reservedRevision)
        {
            return fresh;
        }

        return null;
    }

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
                runtime.Phase = CoordinatorPhase.Complete;
                return await publisher.PublishTerminalAsync(
                    runtime,
                    current,
                    DelegationState.NeedsSupervisor,
                    "Cancellation could not be reconciled because no executable provider adapter is available.",
                    [],
                    cancellationToken,
                    unresolvedConcerns: ["Cancellation remains unresolved because no executable provider adapter is available."])
                    .ConfigureAwait(false);
            }

            runtime.Phase = CoordinatorPhase.Complete;
            return await publisher.PublishTerminalAsync(
                runtime,
                current,
                DelegationState.NeedsSupervisor,
                runtime.ProviderFailure ?? "No executable provider adapter is available; supervisory authority is required.",
                [],
                cancellationToken,
                unresolvedConcerns: ["No executable provider adapter is available; supervisory authority is required."])
                .ConfigureAwait(false);
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

        if (runtime.InFlightProviderCall == current.Progress.Revision)
        {
            // Another pump reserved this revision for provider I/O and has not
            // settled it. Wait (return current) instead of publishing or
            // launching duplicate work; duration already ran above so bounds
            // stay observable through the stalled call.
            return current;
        }

        if (current.Progress.State == DelegationState.Queued)
        {
            current = await publisher.PublishRunningAsync(
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
                // There is no handle to reconcile. Once bounded recovery is
                // exhausted, an unresolved cancellation must become an
                // explicit supervisory outcome rather than live forever.
                return await publisher.PublishCancellationNeedsSupervisorAsync(
                    runtime,
                    current,
                    cancellationToken).ConfigureAwait(false);
            }

            runtime.Phase = CoordinatorPhase.Complete;
            return await publisher.PublishBudgetExceededAsync(
                runtime,
                current,
                [],
                current.Progress.Retries >= runtime.Request.Budget.MaximumRetries
                    ? "retries"
                    : "worker-calls",
                current.Progress.Retries >= runtime.Request.Budget.MaximumRetries
                    ? runtime.Request.Budget.MaximumRetries
                    : runtime.Request.Budget.MaximumWorkerCalls,
                current.Progress.Retries >= runtime.Request.Budget.MaximumRetries
                    ? current.Progress.Retries
                    : current.Progress.WorkerCalls,
                1,
                "The configured provider-start resource limit was exhausted before a handle was captured.",
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            current = await publisher.PublishRunningAsync(
                runtime,
                current,
                workerCalls: current.Progress.WorkerCalls + 1,
                retries: current.Progress.Retries + 1,
                cancellationToken).ConfigureAwait(false);
        }

        var reservedRevision = current.Progress.Revision;
        runtime.InFlightProviderCall = reservedRevision;
        try
        {
            ExternalOperationStartReceipt receipt;
            try
            {
                receipt = await CallProviderOutsideGateAsync(
                    runtime,
                    providerToken => runtime.Adapter.StartAsync(
                        runtime.StartRequest!, providerCaptureSink, providerToken)).ConfigureAwait(false);
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
                return await publisher.PublishTerminalAsync(
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
                return await publisher.PublishTerminalAsync(
                    runtime,
                    current,
                    DelegationState.Failed,
                    UnclassifiedFailureSummary("start"),
                    [],
                    cancellationToken).ConfigureAwait(false);
            }

            // Refresh under the same fence: cancellation may have been recorded
            // while the start call was pending, and another pump may have moved
            // the store meanwhile. A terminal store or any revision movement
            // discards this late outcome instead of overwriting newer state.
            pendingCancellation = runtime.CancellationRequest is not null
                || runtime.CancellationKey is not null;
            var supersededStart = await ReconcileProviderReturnAsync(
                runtime.DelegationId, reservedRevision, cancellationToken).ConfigureAwait(false);
            if (supersededStart is not null)
            {
                return supersededStart;
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
                return await publisher.PublishTerminalAsync(
                    runtime,
                    current,
                    DelegationState.Failed,
                    "Provider start receipt validation failed.",
                    [],
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            runtime.InFlightProviderCall = null;
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
            return await publisher.PublishBudgetExceededAsync(
                runtime,
                current,
                [],
                "worker-calls",
                runtime.Request.Budget.MaximumWorkerCalls,
                current.Progress.WorkerCalls,
                1,
                "The configured worker-call limit was exhausted before observation.",
                cancellationToken).ConfigureAwait(false);
        }

        if (pendingCancellation && !TryReserveCancellationSafetyCall(runtime))
        {
            return await publisher.PublishCancellationNeedsSupervisorAsync(
                runtime,
                current,
                cancellationToken).ConfigureAwait(false);
        }

        if (runtime.InFlightProviderCall == current.Progress.Revision)
        {
            // Another pump reserved this revision for provider I/O and has not
            // settled it. Wait instead of observing twice.
            return current;
        }

        var reservedRevision = current.Progress.Revision;
        runtime.InFlightProviderCall = reservedRevision;
        try
        {
            ExternalOperationObservation observation;
            try
            {
                observation = await CallProviderOutsideGateAsync(
                    runtime,
                    providerToken => runtime.Adapter!.ObserveAsync(runtime.Handle!, providerToken)).ConfigureAwait(false);
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
                    return await publisher.PublishRunningAsync(
                        runtime,
                        current,
                        workerCalls: checked(current.Progress.WorkerCalls + 1),
                        retries: current.Progress.Retries,
                        cancellationToken).ConfigureAwait(false);
                }

                if (exception.Failure.Retryable)
                {
                    return await RetryTransportAsync(runtime, current, CoordinatorPhase.Observe,
                        RetryableFailureSummary("observation", exception.Failure), cancellationToken).ConfigureAwait(false);
                }

                runtime.Phase = CoordinatorPhase.Complete;
                return await publisher.PublishTerminalAsync(runtime, current, DelegationState.Failed,
                    ClassifiedFailureSummary("observation", exception.Failure), [], cancellationToken,
                    current.Progress.WorkerCalls + 1).ConfigureAwait(false);
            }
            catch
            {
                if (pendingCancellation)
                {
                    runtime.Phase = CoordinatorPhase.Observe;
                    return await publisher.PublishRunningAsync(
                        runtime,
                        current,
                        workerCalls: checked(current.Progress.WorkerCalls + 1),
                        retries: current.Progress.Retries,
                        cancellationToken).ConfigureAwait(false);
                }

                runtime.Phase = CoordinatorPhase.Complete;
                return await publisher.PublishTerminalAsync(runtime, current, DelegationState.Failed,
                    UnclassifiedFailureSummary("observation"), [], cancellationToken,
                    current.Progress.WorkerCalls + 1).ConfigureAwait(false);
            }

            // Refresh under the same fence: cancellation may have been recorded
            // while the observation was pending, and another pump may have moved
            // the store meanwhile.
            pendingCancellation = IsCancellationPending(runtime);
            var supersededObservation = await ReconcileProviderReturnAsync(
                runtime.DelegationId, reservedRevision, cancellationToken).ConfigureAwait(false);
            if (supersededObservation is not null)
            {
                return supersededObservation;
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
                return await publisher.PublishTerminalAsync(
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
                var summary = ProviderTerminalSummary("observation", observation.State, observation.Failure);
                return await publisher.PublishTerminalAsync(runtime, current, state, summary, [], cancellationToken, current.Progress.WorkerCalls + 1)
                    .ConfigureAwait(false);
            }

            if (!pendingCancellation
                && observation.State == ExternalOperationState.Unknown
                && observation.Failure is { Retryable: false })
            {
                // The provider reported an ambiguous state that will not be
                // retried. Observing again cannot resolve it, so escalate to
                // the host instead of spending the worker-call budget on
                // no-op observations that end as an unrelated BudgetExceeded.
                runtime.Phase = CoordinatorPhase.Complete;
                return await publisher.PublishTerminalAsync(
                    runtime,
                    current,
                    DelegationState.NeedsSupervisor,
                    "The provider reported an ambiguous state with a non-retryable error; supervisory authority is required to resolve the outcome.",
                    [],
                    cancellationToken,
                    current.Progress.WorkerCalls + 1,
                    unresolvedConcerns:
                    [
                        $"Provider observation reported state 'Unknown' with non-retryable code '{observation.Failure.Code}'.",
                    ]).ConfigureAwait(false);
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
                    return await publisher.PublishRunningAsync(
                        runtime,
                        current,
                        workerCalls: current.Progress.WorkerCalls + 1,
                        retries: current.Progress.Retries,
                        cancellationToken).ConfigureAwait(false);
                }

                var observed = await publisher.PublishRunningAsync(
                    runtime,
                    current,
                    workerCalls: checked(current.Progress.WorkerCalls + 1),
                    retries: current.Progress.Retries,
                    cancellationToken).ConfigureAwait(false);
                return await CancelKnownHandleAsync(runtime, observed, cancellationToken).ConfigureAwait(false);
            }

            runtime.Phase = observation.ResultAvailable || observation.State == ExternalOperationState.Succeeded
                ? CoordinatorPhase.GetResult
                : CoordinatorPhase.Observe;
            if (observation.State == ExternalOperationState.Waiting)
            {
                runtime.Phase = CoordinatorPhase.Observe;
                return await publisher.PublishWaitingAsync(
                    runtime,
                    current,
                    observation,
                    cancellationToken).ConfigureAwait(false);
            }

            return await publisher.PublishRunningAsync(
                runtime,
                current,
                workerCalls: current.Progress.WorkerCalls + 1,
                retries: current.Progress.Retries,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            runtime.InFlightProviderCall = null;
        }
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
            return await publisher.PublishBudgetExceededAsync(
                runtime,
                current,
                [],
                "worker-calls",
                runtime.Request.Budget.MaximumWorkerCalls,
                current.Progress.WorkerCalls,
                1,
                "The configured worker-call limit was exhausted before result retrieval.",
                cancellationToken).ConfigureAwait(false);
        }

        if (runtime.InFlightProviderCall == current.Progress.Revision)
        {
            // Another pump reserved this revision for provider I/O and has not
            // settled it. Wait instead of retrieving twice.
            return current;
        }

        var reservedRevision = current.Progress.Revision;
        runtime.InFlightProviderCall = reservedRevision;
        try
        {
            ExternalOperationResult result;
            runtime.ResultRetrievalStarted = true;
            try
            {
                result = await CallProviderOutsideGateAsync(
                    runtime,
                    providerToken => runtime.Adapter!.GetResultAsync(runtime.Handle!, providerToken)).ConfigureAwait(false);
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
                        RetryableFailureSummary("result", exception.Failure), cancellationToken).ConfigureAwait(false);
                }

                runtime.Phase = CoordinatorPhase.Complete;
                return await publisher.PublishTerminalAsync(runtime, current, DelegationState.Failed,
                    ClassifiedFailureSummary("result", exception.Failure), [], cancellationToken,
                    current.Progress.WorkerCalls + 1).ConfigureAwait(false);
            }
            catch
            {
                runtime.Phase = CoordinatorPhase.Complete;
                return await publisher.PublishTerminalAsync(runtime, current, DelegationState.Failed,
                    UnclassifiedFailureSummary("result"), [], cancellationToken,
                    current.Progress.WorkerCalls + 1).ConfigureAwait(false);
            }

            // Reconcile under the same fence: a terminal store or any revision
            // movement discards this late result instead of overwriting newer
            // state. Confirmed provider results stay authoritative here; a
            // recorded cancellation does not rewrite them.
            var supersededResult = await ReconcileProviderReturnAsync(
                runtime.DelegationId, reservedRevision, cancellationToken).ConfigureAwait(false);
            if (supersededResult is not null)
            {
                return supersededResult;
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
                return await publisher.PublishTerminalAsync(
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
                if (runtime.CorrectionStarted
                    && runtime.Candidate is { Revision: > 1 })
                {
                    // A post-cancellation reconciliation may retrieve the
                    // provider's original result again. Preserve the already
                    // published corrected candidate and only re-enter its evaluator.
                    runtime.Phase = CoordinatorPhase.Evaluate;
                    return current;
                }

                if (result.Candidate is null)
                {
                    runtime.Phase = CoordinatorPhase.Complete;
                    return await publisher.PublishTerminalAsync(
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
                    runtime.ResultArtifacts = result.Artifacts;
                    runtime.ValidationInvocationId = $"validation:{runtime.DelegationId.Value:D}:1";
                    runtime.ReviewInvocationId = $"review:{runtime.DelegationId.Value:D}:1";
                    runtime.ImplementationInvocation = CreateImplementationInvocation(runtime, publication.Candidate, result);

                    // The hard worker-call budget is a runtime contract: never
                    // launch evaluators the budget cannot pay for.
                    var consumedAtResult = DelegationExecutionPublisher.ActualWorkerCalls(runtime, current);
                    var verificationCalls = CandidateEvaluationRunner.VerificationCallCount(
                        candidateValidator is not null,
                        candidateReviewer is not null);
                    var projectedCalls = checked(consumedAtResult + verificationCalls);
                    if (projectedCalls > runtime.Request.Budget.MaximumWorkerCalls)
                    {
                        runtime.Phase = CoordinatorPhase.Complete;
                        return await publisher.PublishBudgetExceededAsync(
                            runtime,
                            current,
                            result.Artifacts,
                            "worker-calls",
                            runtime.Request.Budget.MaximumWorkerCalls,
                            consumedAtResult,
                            projectedCalls - consumedAtResult,
                            $"The worker-call budget cannot cover deterministic validation and independent review (required {projectedCalls}, limit {runtime.Request.Budget.MaximumWorkerCalls}).",
                            cancellationToken).ConfigureAwait(false);
                    }

                    runtime.Phase = CoordinatorPhase.Evaluate;
                    return current;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    runtime.Phase = CoordinatorPhase.Complete;
                    return await publisher.PublishTerminalAsync(
                        runtime,
                        current,
                        DelegationState.Failed,
                        "Candidate publication failed coordinator validation.",
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
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    runtime.Phase = CoordinatorPhase.Complete;
                    return await publisher.PublishTerminalAsync(
                        runtime,
                        current,
                        DelegationState.Failed,
                        "Candidate publication failed coordinator validation.",
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
            var summary = result.State == ExternalOperationState.Succeeded
                ? "The provider completed the operation successfully."
                : ProviderTerminalSummary("result", result.State, result.Failure);
            return await publisher.PublishTerminalAsync(
                runtime,
                current,
                state,
                summary,
                result.Artifacts,
                cancellationToken,
                current.Progress.WorkerCalls + 1).ConfigureAwait(false);
        }
        finally
        {
            runtime.InFlightProviderCall = null;
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
            "delegation",
            runtime.Correlation.Agent.Provider,
            null,
            runtime.Correlation.Agent.Identifier,
            [],
            [],
            result.Artifacts,
            candidate,
            executionCorrelation: runtime.Correlation);
    }

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
            return await publisher.PublishBudgetExceededAsync(
                runtime,
                current,
                [],
                current.Progress.Retries >= runtime.Request.Budget.MaximumRetries
                    ? "retries"
                    : "worker-calls",
                current.Progress.Retries >= runtime.Request.Budget.MaximumRetries
                    ? runtime.Request.Budget.MaximumRetries
                    : runtime.Request.Budget.MaximumWorkerCalls,
                current.Progress.Retries >= runtime.Request.Budget.MaximumRetries
                    ? current.Progress.Retries
                    : current.Progress.WorkerCalls,
                1,
                $"{summary} The configured retry or worker-call limit was exhausted.",
                cancellationToken,
                workerCalls: phase == CoordinatorPhase.Start
                    ? current.Progress.WorkerCalls
                    : checked(current.Progress.WorkerCalls + 1)).ConfigureAwait(false);
        }

        runtime.Phase = phase;
        return await publisher.PublishRunningAsync(
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
            return await publisher.PublishCancellationNeedsSupervisorAsync(
                runtime,
                current,
                cancellationToken).ConfigureAwait(false);
        }

        runtime.CancellationIntentOnly = false;
        runtime.CancellationAttempted = true;
        current = await publisher.PublishRunningAsync(
            runtime,
            current,
            checked(current.Progress.WorkerCalls + 1),
            current.Progress.Retries,
            cancellationToken).ConfigureAwait(false);

        // Cancellation intent was already recorded by the caller; this marker
        // only fences concurrent pumps, it never blocks the cancel itself.
        var reservedRevision = current.Progress.Revision;
        runtime.InFlightProviderCall = reservedRevision;
        try
        {
            ExternalOperationCancellationReceipt receipt;
            try
            {
                receipt = await CallProviderOutsideGateAsync(
                    runtime,
                    providerToken => runtime.Adapter!.CancelAsync(
                    new ExternalOperationCancelRequest(
                        runtime.Handle!,
                            runtime.CancellationRequest!.CancellationKey,
                            runtime.CancellationRequest.Reason),
                        providerToken)).ConfigureAwait(false);
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

            // Reconcile under the same fence: a terminal store or any revision
            // movement discards this late receipt instead of overwriting newer
            // state.
            var supersededCancel = await ReconcileProviderReturnAsync(
                runtime.DelegationId, reservedRevision, cancellationToken).ConfigureAwait(false);
            if (supersededCancel is not null)
            {
                return supersededCancel;
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
        finally
        {
            runtime.InFlightProviderCall = null;
        }
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
                runtime.CancellationReconciled = true;
                runtime.Phase = CoordinatorPhase.Complete;
                return await publisher.PublishTerminalAsync(
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
                        runtime.CancellationReconciled = true;
                        runtime.Phase = CoordinatorPhase.Complete;
                        return await publisher.PublishTerminalAsync(
                            runtime,
                            current,
                            DelegationState.Cancelled,
                            "The provider reported the operation was already cancelled.",
                            [],
                            cancellationToken).ConfigureAwait(false);
                    case ExternalOperationState.Succeeded:
                        runtime.CancellationReconciled = true;
                        runtime.Phase = CoordinatorPhase.GetResult;
                        return current;
                    default:
                        runtime.Phase = CoordinatorPhase.Complete;
                        return await publisher.PublishTerminalAsync(
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

        if (runtime.InFlightProviderCall == current.Progress.Revision)
        {
            // Another pump reserved this revision for provider I/O and has not
            // settled it. Wait instead of resuming twice.
            return current;
        }

        var previousHandle = runtime.ResumePreviousHandle ?? runtime.Handle!;
        var reservedRevision = current.Progress.Revision;
        runtime.InFlightProviderCall = reservedRevision;
        try
        {
            ExternalOperationResumeReceipt receipt;
            try
            {
                receipt = await CallProviderOutsideGateAsync(
                    runtime,
                    providerToken => runtime.Adapter!.ResumeAsync(
                        runtime.ResumeRequest!,
                        new ResumeHandleCaptureSink(this, runtime, previousHandle),
                        providerToken)).ConfigureAwait(false);
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

            // Reconcile under the same fence: a terminal store or any revision
            // movement discards this late receipt instead of overwriting newer
            // state.
            var supersededResume = await ReconcileProviderReturnAsync(
                runtime.DelegationId, reservedRevision, cancellationToken).ConfigureAwait(false);
            if (supersededResume is not null)
            {
                return supersededResume;
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
                return await publisher.PublishTerminalAsync(
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
        finally
        {
            runtime.InFlightProviderCall = null;
        }
    }


    private async ValueTask CaptureReturnedHandleAsync(
        ExternalOperationStartReceipt receipt,
        CancellationToken cancellationToken)
    {
        await providerCaptureSink.CaptureAsync(
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
        return await publisher.PublishBudgetExceededAsync(
            runtime,
            current,
            [],
            "duration",
            maximum.Value.Ticks,
            Math.Max(0, elapsed.Ticks),
            1,
            "Maximum delegation duration exceeded.",
            cancellationToken).ConfigureAwait(false);
    }

    private static string ClassifiedFailureSummary(string operation, ExternalOperationFailure failure) =>
        failure.Kind == ExternalOperationFailureKind.Rejection
            ? $"Provider {operation} rejected the operation (classified code '{failure.Code}')."
            : $"Provider {operation} failed with non-retryable classified {failure.Kind.ToString().ToLowerInvariant()} error (code '{failure.Code}').";

    private static string RetryableFailureSummary(string operation, ExternalOperationFailure failure) =>
        $"Provider {operation} reported a retryable classified {failure.Kind.ToString().ToLowerInvariant()} error (code '{failure.Code}').";

    private static string ProviderTerminalSummary(
        string operation,
        ExternalOperationState state,
        ExternalOperationFailure? failure)
    {
        if (state == ExternalOperationState.Cancelled)
        {
            return $"Provider {operation} reported the operation was cancelled.";
        }

        if (failure is not null)
        {
            return ClassifiedFailureSummary(operation, failure);
        }

        return $"Provider {operation} reported terminal state '{state}'.";
    }

    private static string UnclassifiedFailureSummary(string operation) =>
        $"Provider {operation} failed with an unclassified error.";

    private static bool InterventionsEqual(SupervisorIntervention left, SupervisorIntervention right) =>
        left.DelegationId == right.DelegationId
        && left.CheckpointId == right.CheckpointId
        && string.Equals(left.InterventionKey, right.InterventionKey, StringComparison.Ordinal)
        && left.ExpectedRevision == right.ExpectedRevision
        && SupervisorInterventionIdentity.Compute(left) == SupervisorInterventionIdentity.Compute(right);

    internal enum CoordinatorPhase
    {
        Start,
        Observe,
        GetResult,
        Evaluate,
        Complete,
    }

    /// <summary>
    /// Fans accepted handle captures out to the in-memory registry and one
    /// host-owned durable witness, in that order. The registry stays the live
    /// read authority; the witness is write-only evidence for host recovery and
    /// must tolerate exact replays per the sink contract. A witness failure
    /// propagates to the caller, which fails the start closed rather than
    /// advancing to observation with an unpersisted handle.
    /// </summary>
    private sealed class WitnessingHandleCaptureSink(
        InMemoryExternalOperationHandleCaptureRegistry registry,
        IExternalOperationHandleCaptureSink witness) : IExternalOperationHandleCaptureSink
    {
        public async ValueTask CaptureAsync(
            ExternalOperationHandleCapture capture,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(capture);
            await registry.CaptureAsync(capture, cancellationToken).ConfigureAwait(false);
            await witness.CaptureAsync(capture, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class ResumeHandleCaptureSink(InMemoryDelegationCoordinator owner,
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
        await providerCaptureSink.CaptureAsync(capture, cancellationToken).ConfigureAwait(false);
        runtime.Handle = capture.Handle;
        if (rotated)
        {
            runtime.LastObservation = null;
            runtime.ResumeAcceptedAmbiguity = true;
            runtime.ResumeAmbiguityObserved = false;
        }
    }

    internal sealed class RuntimeState
    {
        internal RuntimeState(
            DelegationId delegationId,
            DelegationRequest request,
            ProviderDescriptor? provider,
            IExternalOperationProvider? adapter,
            string? providerFailure,
            IExternalOperationSemanticFingerprintVerifier fingerprintVerifier,
            DateTimeOffset acceptedAt,
            MaterializedDelegationInputs? materialized = null)
        {
            DelegationId = delegationId;
            Request = request;
            Adapter = adapter;
            ProviderFailure = providerFailure;
            AcceptedAt = acceptedAt == default
                ? throw new ArgumentException("The accepted timestamp is required.", nameof(acceptedAt))
                : acceptedAt;
            Phase = CoordinatorPhase.Start;
            Generation = new NodeGenerationId(delegationId.Value);

            if (provider is not null)
            {
                // The trusted agent record comes from host materialization;
                // without it the legacy provider-name-derived record applies.
                // Either way the exact record is fingerprinted into the start
                // identity, and a materialized agent for another provider is
                // refused before any launch.
                var agent = materialized?.Agent
                    ?? new ExternalAgentReference(
                        provider.Provider,
                        provider.Provider,
                        AgentProtocolVersion);
                if (!string.Equals(agent.Provider, provider.Provider, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The materialized agent must belong to the resolved provider.");
                }

                Correlation = new ExternalOperationCorrelation(
                    delegationId,
                    new WorkflowRunExecutionReference(
                        "qingniao-inmemory",
                        delegationId.Value.ToString("N"),
                        "epoch-1"),
                    new StructuralNodeReference("delegation"),
                    Generation,
                    "attempt-1",
                    agent);
                var semanticInput = new ExternalOperationSemanticInputEnvelope(
                    delegationId,
                    agent,
                    AgentCapability,
                    materialized?.InputArtifacts ?? [],
                    materialized?.Budget,
                    materialized?.Deadline);
                StartIdentity = new ExternalOperationStartIdentity(
                    delegationId,
                    Correlation.WorkflowRun,
                    Correlation.StructuralNode,
                    Correlation.NodeGeneration,
                    Correlation.ExecutionAttemptId,
                    $"qingniao:{delegationId.Value:D}:delegation:attempt-1",
                    fingerprintVerifier.Compute(semanticInput));
                StartRequest = new ExternalOperationStartRequest(
                    StartIdentity,
                    Correlation,
                    AgentCapability,
                    materialized?.InputArtifacts ?? [],
                    materialized?.Budget,
                    materialized?.Deadline);
                StartRequest.VerifySemanticFingerprint(fingerprintVerifier);
                StartRequest.VerifySemanticFingerprint(fingerprintVerifier);
            }
            else
            {
                Correlation = null!;
            }
        }

        internal DelegationId DelegationId { get; }
        internal DelegationRequest Request { get; }
        internal IExternalOperationProvider? Adapter { get; }
        internal string? ProviderFailure { get; }
        internal DateTimeOffset AcceptedAt { get; }
        internal SemaphoreSlim Gate { get; } = new(1, 1);
        internal CoordinatorPhase Phase { get; set; }
        internal ExternalOperationCorrelation Correlation { get; }
        internal NodeGenerationId Generation { get; }
        internal ExternalOperationStartIdentity? StartIdentity { get; }
        internal ExternalOperationStartRequest? StartRequest { get; set; }

        /// <summary>
        /// Gets or sets the opaque host execution attachment for this
        /// generation. Qingniao stores and re-presents it but never interprets
        /// it; the host that produced its kind resolves it.
        /// </summary>
        internal DelegationExecutionAttachment? ExecutionAttachment { get; set; }
        internal ExternalOperationHandle? Handle { get; set; }

        /// <summary>
        /// Gets the store revision reserved for in-flight provider I/O, if
        /// any. A second pump at the same revision waits instead of launching
        /// duplicate provider work; duration and budget checks always run
        /// first so bounds stay observable through a stalled call.
        /// </summary>
        internal long? InFlightProviderCall { get; set; }
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
