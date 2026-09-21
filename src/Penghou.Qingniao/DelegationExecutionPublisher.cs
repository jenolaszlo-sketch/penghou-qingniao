namespace Penghou.Qingniao;

/// <summary>
/// Execution-storage publication for one delegation: progress, waiting
/// checkpoints, terminal results, and budget outcomes. Pure publication
/// mechanics; supervision decisions live in the coordinator and the
/// candidate-evaluation runner.
/// </summary>
internal sealed class DelegationExecutionPublisher
{
    private readonly InMemoryDelegationExecutionStore executionStore;
    private readonly Func<DateTimeOffset> now;

    internal DelegationExecutionPublisher(
        InMemoryDelegationExecutionStore executionStore,
        Func<DateTimeOffset>? now = null)
    {
        this.executionStore = executionStore ?? throw new ArgumentNullException(nameof(executionStore));
        this.now = now ?? (() => DateTimeOffset.UtcNow);
    }

    internal async ValueTask<DelegationExecutionSnapshot> PublishRunningAsync(
        InMemoryDelegationCoordinator.RuntimeState runtime,
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
            ["delegation"],
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

    internal async ValueTask<DelegationExecutionSnapshot> PublishWaitingAsync(
        InMemoryDelegationCoordinator.RuntimeState runtime,
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
        // DelegationRequest intentionally has no evidence-session field. This
        // deterministic placeholder is only an in-memory correlation value;
        // it is not a durable or authoritative session identity.
        var session = new SupervisionSessionReference(
            $"qingniao-inmemory-session:{runtime.DelegationId.Value:D}");
        var checkpoint = new SupervisorCheckpointDescriptor(
            checkpointId,
            session,
            runtime.DelegationId,
            runtime.Request.AdmissionFence,
            runtime.Correlation.WorkflowRun,
            runtime.Correlation.StructuralNode,
            new NodeGenerationId(runtime.Correlation.NodeGeneration.Value),
            checked(current.Progress.Revision + 1),
            dependentProgressGated: true);
        var progress = new DelegationProgress(
            runtime.DelegationId,
            DelegationState.WaitingForSupervisor,
            checked(current.Progress.Revision + 1),
            ["delegation"],
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

    internal async ValueTask<DelegationExecutionSnapshot> PublishTerminalAsync(
        InMemoryDelegationCoordinator.RuntimeState runtime,
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
            ["delegation"],
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

    internal ValueTask<DelegationExecutionSnapshot> PublishCancellationNeedsSupervisorAsync(
        InMemoryDelegationCoordinator.RuntimeState runtime,
        DelegationExecutionSnapshot current,
        CancellationToken cancellationToken) =>
        PublishTerminalAsync(
            runtime,
            current,
            DelegationState.NeedsSupervisor,
            "Cancellation remains unresolved after the coordinator safety observation ceiling.",
            [],
            cancellationToken);

    internal ValueTask<DelegationExecutionSnapshot> PublishBudgetExceededAsync(
        InMemoryDelegationCoordinator.RuntimeState runtime,
        DelegationExecutionSnapshot current,
        IReadOnlyList<DelegationArtifactReference> artifacts,
        string dimension,
        long limit,
        long actualConsumed,
        long refusedAmount,
        string reason,
        CancellationToken cancellationToken,
        int? workerCalls = null)
    {
        var actual = dimension == "duration"
            ? BudgetQuantity.Ticks(actualConsumed)
            : BudgetQuantity.Count(actualConsumed);
        var limitQuantity = dimension == "duration"
            ? BudgetQuantity.Ticks(limit)
            : BudgetQuantity.Count(limit);
        if (refusedAmount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(refusedAmount), "A refused budget charge must be positive.");
        }

        var refusedQuantity = dimension == "duration"
            ? BudgetQuantity.Ticks(refusedAmount)
            : BudgetQuantity.Count(refusedAmount);
        var aggregate = new BudgetQuantity(
            actual.Kind,
            checked(actual.Value + refusedQuantity.Value),
            actual.Currency);
        var outcome = new BudgetExceededOutcome(
            runtime.DelegationId,
            "qingniao-runtime-budget-v1",
            new BudgetCharge(dimension, refusedQuantity),
            limitQuantity,
            aggregate,
            actual,
            new BudgetCharge(dimension, refusedQuantity),
            DeterministicBudgetDecisionId(runtime.DelegationId, dimension, actualConsumed, limit),
            null,
            reason,
            Later(current.Progress.UpdatedAt, RequireNow()));
        return PublishTerminalAsync(
            runtime,
            current,
            DelegationState.BudgetExceeded,
            reason,
            artifacts,
            cancellationToken,
            workerCalls ?? ActualWorkerCalls(runtime, current),
            outcome);
    }

    internal static int ActualWorkerCalls(
        InMemoryDelegationCoordinator.RuntimeState runtime,
        DelegationExecutionSnapshot current) =>
        checked(current.Progress.WorkerCalls
            + (runtime.ResultRetrievalStarted ? 1 : 0)
            + runtime.EvaluationCallsStarted
            + (runtime.CorrectionInvocationStarted ? 1 : 0));

    internal static DateTimeOffset Later(DateTimeOffset left, DateTimeOffset right) =>
        left >= right ? left : right;

    internal static string NormalizeFailure(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? "The provider operation failed."
            : value.Trim().Length <= 16_384
                ? value.Trim()
                : value.Trim()[..16_384];

    internal static string NormalizeConcern(string value)
    {
        var normalized = NormalizeFailure(value);
        const int maximumConcernLength = 4_096;
        return normalized.Length <= maximumConcernLength
            ? normalized
            : normalized[..maximumConcernLength];
    }

    private static Guid DeterministicBudgetDecisionId(DelegationId delegationId, string dimension, long consumed, long limit)
    {
        var payload = $"{delegationId.Value:D}|{dimension}|{consumed}|{limit}";
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload));
        return new Guid(hash.AsSpan(0, 16));
    }

    private DateTimeOffset RequireNow()
    {
        var value = now();
        return value == default
            ? throw new InvalidOperationException("The coordinator clock returned a default timestamp.")
            : value;
    }
}
