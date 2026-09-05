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
    private readonly Func<DateTimeOffset> now;

    internal InMemoryDelegationCoordinator(
        IDelegationAcceptanceRegistry acceptanceRegistry,
        IWorkflowPlanResolver planResolver,
        IProviderRegistry providerRegistry,
        InMemoryExternalOperationProviderCatalog adapterCatalog,
        SimingExternalOperationSemanticFingerprintVerifier fingerprintVerifier,
        InMemoryExternalOperationHandleCaptureRegistry? handleRegistry = null,
        InMemoryDelegationExecutionStore? executionStore = null,
        Func<DateTimeOffset>? now = null)
    {
        this.acceptanceRegistry = acceptanceRegistry ?? throw new ArgumentNullException(nameof(acceptanceRegistry));
        this.planResolver = planResolver ?? throw new ArgumentNullException(nameof(planResolver));
        this.providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));
        this.adapterCatalog = adapterCatalog ?? throw new ArgumentNullException(nameof(adapterCatalog));
        this.fingerprintVerifier = fingerprintVerifier ?? throw new ArgumentNullException(nameof(fingerprintVerifier));
        this.handleRegistry = handleRegistry ?? new InMemoryExternalOperationHandleCaptureRegistry();
        this.executionStore = executionStore ?? new InMemoryDelegationExecutionStore();
        this.now = now ?? (() => DateTimeOffset.UtcNow);
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
        try
        {
            var current = await executionStore.GetAsync(delegationId, cancellationToken).ConfigureAwait(false);
            if (current.Progress.Revision != expectedRevision)
            {
                throw new DelegationExecutionStaleException(
                    delegationId,
                    current.Progress.Revision,
                    expectedRevision);
            }

            if (DelegationLifecycle.IsTerminal(current.Progress.State))
            {
                return current;
            }

            return runtime.Phase switch
            {
                CoordinatorPhase.Start => await PumpStartAsync(runtime, current, cancellationToken).ConfigureAwait(false),
                CoordinatorPhase.Observe => await PumpObserveAsync(runtime, current, cancellationToken).ConfigureAwait(false),
                CoordinatorPhase.GetResult => await PumpResultAsync(runtime, current, cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidOperationException("The in-memory coordinator has an unknown private phase."),
            };
        }
        finally
        {
            runtime.Gate.Release();
        }
    }

    private async ValueTask<DelegationExecutionSnapshot> PumpStartAsync(
        RuntimeState runtime,
        DelegationExecutionSnapshot current,
        CancellationToken cancellationToken)
    {
        if (runtime.Adapter is null)
        {
            runtime.Phase = CoordinatorPhase.Complete;
            return await PublishTerminalAsync(
                runtime,
                current,
                DelegationState.Failed,
                runtime.ProviderFailure ?? "No executable provider adapter is available.",
                [],
                cancellationToken).ConfigureAwait(false);
        }

        var durationExceeded = await EnforceDurationAsync(runtime, current, cancellationToken).ConfigureAwait(false);
        if (durationExceeded is not null)
        {
            return durationExceeded;
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
                return current;
            }

            if (exception.Failure.Retryable)
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
            return current;
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
                "The worker-call budget was exhausted before observation.",
                [],
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

        runtime.Phase = observation.ResultAvailable || observation.State == ExternalOperationState.Succeeded
            ? CoordinatorPhase.GetResult
            : CoordinatorPhase.Observe;
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
        return await executionStore.PublishProgressAsync(
            runtime.DelegationId,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<DelegationExecutionSnapshot> PublishTerminalAsync(
        RuntimeState runtime,
        DelegationExecutionSnapshot current,
        DelegationState state,
        string summary,
        IReadOnlyList<DelegationArtifactReference> artifacts,
        CancellationToken cancellationToken,
        int? workerCalls = null,
        BudgetExceededOutcome? budgetExceeded = null)
    {
        var timestamp = Later(current.Progress.UpdatedAt, RequireNow());
        var progress = new DelegationProgress(
            runtime.DelegationId,
            state,
            checked(current.Progress.Revision + 1),
            [],
            ["implement"],
            workerCalls ?? current.Progress.WorkerCalls,
            current.Progress.Retries,
            timestamp);
        var result = new DelegationResult(
            runtime.DelegationId,
            state,
            NormalizeFailure(summary),
            new DelegationEvidence([], [], 0, 0, null, 0),
            artifacts,
            state == DelegationState.Completed ? [] : [NormalizeFailure(summary)],
            timestamp,
            budgetExceeded: budgetExceeded);
        return await executionStore.PublishTerminalAsync(
            runtime.DelegationId,
            progress,
            result,
            cancellationToken).ConfigureAwait(false);
    }

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
        string.IsNullOrWhiteSpace(value) ? "The provider operation failed." : value.Trim();

    private enum CoordinatorPhase
    {
        Start,
        Observe,
        GetResult,
        Complete,
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
    }
}
