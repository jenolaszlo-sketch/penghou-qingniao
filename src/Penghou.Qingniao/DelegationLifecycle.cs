namespace Penghou.Qingniao;

/// <summary>Raised when a delegation snapshot violates the lifecycle contract.</summary>
public sealed class DelegationLifecycleViolationException : InvalidOperationException
{
    /// <summary>Initializes an exception describing an invalid lifecycle operation.</summary>
    public DelegationLifecycleViolationException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Pure lifecycle and snapshot validation for the fixed Qingniao strategy.
/// Providers and durable stores may use these rules without depending on one
/// another or on a particular persistence technology.
/// </summary>
public static class DelegationLifecycle
{
    /// <summary>Returns whether the supplied state is terminal under the fixed strategy.</summary>
    public static bool IsTerminal(DelegationState state)
    {
        EnsureDefined(state);
        return state is
            DelegationState.Completed
            or DelegationState.Failed
            or DelegationState.Cancelled
            or DelegationState.BudgetExceeded
            or DelegationState.NeedsSupervisor;
    }

    /// <summary>Returns whether a direct transition between two defined states is allowed.</summary>
    public static bool CanTransition(DelegationState current, DelegationState next)
    {
        EnsureDefined(current);
        EnsureDefined(next);
        return (current, next) switch
        {
            (DelegationState.Queued, DelegationState.Running) => true,
            (DelegationState.Queued, DelegationState.Failed) => true,
            (DelegationState.Queued, DelegationState.Cancelled) => true,
            (DelegationState.Queued, DelegationState.BudgetExceeded) => true,
            (DelegationState.Queued, DelegationState.NeedsSupervisor) => true,
            (DelegationState.Queued, DelegationState.WaitingForSupervisor) => true,
            (DelegationState.Running, DelegationState.Completed) => true,
            (DelegationState.Running, DelegationState.Failed) => true,
            (DelegationState.Running, DelegationState.Cancelled) => true,
            (DelegationState.Running, DelegationState.BudgetExceeded) => true,
            (DelegationState.Running, DelegationState.NeedsSupervisor) => true,
            (DelegationState.Running, DelegationState.WaitingForSupervisor) => true,
            (DelegationState.WaitingForSupervisor, DelegationState.Running) => true,
            (DelegationState.WaitingForSupervisor, DelegationState.Failed) => true,
            (DelegationState.WaitingForSupervisor, DelegationState.Cancelled) => true,
            (DelegationState.WaitingForSupervisor, DelegationState.BudgetExceeded) => true,
            (DelegationState.WaitingForSupervisor, DelegationState.NeedsSupervisor) => true,
            _ => false,
        };
    }

    /// <summary>Validates a direct state transition.</summary>
    /// <exception cref="DelegationLifecycleViolationException">Thrown when the transition is not allowed.</exception>
    public static void EnsureTransition(DelegationState current, DelegationState next)
    {
        if (!CanTransition(current, next))
        {
            throw new DelegationLifecycleViolationException(
                $"Delegation state cannot transition from '{current}' to '{next}'.");
        }
    }

    /// <summary>
    /// Validates a progress snapshot and, when supplied, its predecessor.
    /// Revisions are non-negative and non-decreasing. Reusing a revision is
    /// allowed only for an identical snapshot; a changed snapshot needs a new
    /// revision. Counters and timestamps never move backwards.
    /// </summary>
    /// <summary>Validates a progress snapshot and its optional predecessor.</summary>
    public static void ValidateProgress(DelegationProgress progress, DelegationProgress? previous = null)
    {
        ArgumentNullException.ThrowIfNull(progress);
        ValidateSnapshot(progress);

        if (previous is null)
        {
            return;
        }

        ValidateSnapshot(previous);

        if (progress.DelegationId != previous.DelegationId)
        {
            throw new DelegationLifecycleViolationException("Progress snapshots must belong to the same delegation.");
        }

        if (progress.Revision < previous.Revision)
        {
            throw new DelegationLifecycleViolationException("Progress revision cannot move backwards.");
        }

        if (progress.WorkerCalls < previous.WorkerCalls || progress.Retries < previous.Retries)
        {
            throw new DelegationLifecycleViolationException("Progress counters cannot move backwards.");
        }

        if (progress.UpdatedAt < previous.UpdatedAt)
        {
            throw new DelegationLifecycleViolationException("Progress timestamp cannot move backwards.");
        }

        if (progress.Revision == previous.Revision)
        {
            if (!SnapshotsEqual(progress, previous))
            {
                throw new DelegationLifecycleViolationException(
                    "A changed progress snapshot must use a greater revision.");
            }

            return;
        }

        if (previous.State == progress.State)
        {
            if (IsTerminal(previous.State))
            {
                throw new DelegationLifecycleViolationException(
                    $"Terminal state '{previous.State}' cannot change after it is recorded.");
            }

            if (previous.State == DelegationState.WaitingForSupervisor)
            {
                EnsureCheckpointStable(previous.Checkpoint, progress.Checkpoint);
            }

            return;
        }

        EnsureTransition(previous.State, progress.State);
    }

    /// <summary>
    /// Validates a terminal result independently of its storage provider.
    /// `NeedsSupervisor` is terminal for the fixed strategy, not an implicit
    /// continuation request.
    /// </summary>
    /// <summary>Validates a terminal result and its immutable evidence.</summary>
    public static void ValidateResult(DelegationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        EnsureDefined(result.State);
        if (!IsTerminal(result.State))
        {
            throw new DelegationLifecycleViolationException(
                $"A result cannot be published for non-terminal state '{result.State}'.");
        }

        if (string.IsNullOrWhiteSpace(result.Summary) || result.Summary.Length > 16_384)
        {
            throw new DelegationLifecycleViolationException("A result summary must contain 1 to 16,384 characters.");
        }

        if (result.Evidence is null)
        {
            throw new DelegationLifecycleViolationException("A terminal result must contain evidence.");
        }

        if (result.Evidence.TestsPassed < 0
            || result.Evidence.TestsFailed < 0
            || result.Evidence.ReviewFindingsResolved < 0)
        {
            throw new DelegationLifecycleViolationException("Result evidence counters cannot be negative.");
        }

        if (result.CompletedAt == default)
        {
            throw new DelegationLifecycleViolationException("A terminal result must have a completion timestamp.");
        }

        if (result.State == DelegationState.BudgetExceeded)
        {
            if (result.BudgetExceeded is null)
            {
                throw new DelegationLifecycleViolationException(
                    "A BudgetExceeded result must include durable budget accounting evidence.");
            }

            if (result.BudgetExceeded.DelegationId != result.DelegationId)
            {
                throw new DelegationLifecycleViolationException(
                    "BudgetExceeded accounting evidence must belong to the result delegation.");
            }
        }
        else if (result.BudgetExceeded is not null)
        {
            throw new DelegationLifecycleViolationException(
                "Budget accounting evidence is valid only for a BudgetExceeded result.");
        }
    }

    /// <summary>
    /// A result is available exactly when the current snapshot is terminal and
    /// has a matching, valid result. Non-terminal snapshots must not expose one.
    /// </summary>
    /// <summary>Ensures result presence agrees with the terminal state of a progress snapshot.</summary>
    public static void ValidateResultAvailability(DelegationProgress progress, DelegationResult? result)
    {
        ArgumentNullException.ThrowIfNull(progress);
        ValidateProgress(progress);

        if (result is null)
        {
            if (IsTerminal(progress.State))
            {
                throw new DelegationLifecycleViolationException(
                    $"Terminal state '{progress.State}' must have a result.");
            }

            return;
        }

        if (!IsTerminal(progress.State))
        {
            throw new DelegationLifecycleViolationException(
                $"Non-terminal state '{progress.State}' cannot have a result.");
        }

        ValidateResult(result);
        if (result.DelegationId != progress.DelegationId || result.State != progress.State)
        {
            throw new DelegationLifecycleViolationException(
                "The terminal result must match the terminal progress identity and state.");
        }

        if (result.CompletedAt < progress.UpdatedAt)
        {
            throw new DelegationLifecycleViolationException(
                "The terminal result cannot precede the terminal progress timestamp.");
        }
    }

    /// <summary>
    /// Validates an attempted result publication. The first valid terminal
    /// result is accepted; an exact semantic replay is idempotent, while any
    /// replacement is rejected so terminal evidence cannot be rewritten.
    /// Durable stores must enforce this check atomically with terminal state.
    /// </summary>
    /// <summary>Validates first-write, idempotent replay, and immutable replacement semantics for results.</summary>
    public static void ValidateResultPublication(DelegationResult? existing, DelegationResult candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ValidateResult(candidate);
        if (existing is null)
        {
            return;
        }

        ValidateResult(existing);
        if (!ResultsEqual(existing, candidate))
        {
            throw new DelegationLifecycleViolationException(
                "A terminal result cannot be replaced after publication.");
        }
    }

    private static bool SnapshotsEqual(DelegationProgress left, DelegationProgress right) =>
        left.DelegationId == right.DelegationId
        && left.State == right.State
        && left.Revision == right.Revision
        && left.WorkerCalls == right.WorkerCalls
        && left.Retries == right.Retries
        && left.UpdatedAt == right.UpdatedAt
        && (left.Checkpoint is null
            ? right.Checkpoint is null
            : right.Checkpoint is not null && CheckpointEqual(left.Checkpoint, right.Checkpoint))
        && left.CurrentSteps.SequenceEqual(right.CurrentSteps, StringComparer.Ordinal)
        && left.CompletedSteps.SequenceEqual(right.CompletedSteps, StringComparer.Ordinal);

    private static bool CheckpointEqual(
        SupervisorCheckpointDescriptor left,
        SupervisorCheckpointDescriptor right) =>
        CheckpointIdentityEqual(left, right)
        && left.ExpectedObservableRevision == right.ExpectedObservableRevision;

    private static bool CheckpointIdentityEqual(
        SupervisorCheckpointDescriptor left,
        SupervisorCheckpointDescriptor right) =>
        left.CheckpointId == right.CheckpointId
        && left.Session == right.Session
        && left.DelegationId == right.DelegationId
        && left.PlanRevision == right.PlanRevision
        && left.WorkflowRun == right.WorkflowRun
        && left.StructuralNode == right.StructuralNode
        && left.NodeGeneration == right.NodeGeneration
        && left.DependentProgressGated == right.DependentProgressGated;

    private static bool ResultsEqual(DelegationResult left, DelegationResult right) =>
        left.DelegationId == right.DelegationId
        && left.State == right.State
        && string.Equals(left.Summary, right.Summary, StringComparison.Ordinal)
        && left.Evidence.TestsPassed == right.Evidence.TestsPassed
        && left.Evidence.TestsFailed == right.Evidence.TestsFailed
        && left.Evidence.ReviewApproved == right.Evidence.ReviewApproved
        && left.Evidence.ReviewFindingsResolved == right.Evidence.ReviewFindingsResolved
        && left.Evidence.ChangedFiles.SequenceEqual(right.Evidence.ChangedFiles, StringComparer.Ordinal)
        && left.Evidence.Commands.SequenceEqual(right.Evidence.Commands, StringComparer.Ordinal)
        && EvidenceBundleIdentity.SemanticallyEqual(left.NormalizedEvidence, right.NormalizedEvidence)
        && ResultReferenceEqual(left.ResultReference, right.ResultReference)
        && BudgetOutcomeEqual(left.BudgetExceeded, right.BudgetExceeded)
        && left.Artifacts.SequenceEqual(right.Artifacts)
        && left.UnresolvedConcerns.SequenceEqual(right.UnresolvedConcerns, StringComparer.Ordinal)
        && left.CompletedAt == right.CompletedAt;

    private static bool BudgetOutcomeEqual(BudgetExceededOutcome? left, BudgetExceededOutcome? right)
    {
        if (left is null || right is null) return left is null && right is null;
        return left.DelegationId == right.DelegationId
            && string.Equals(left.DefinitionVersion, right.DefinitionVersion, StringComparison.Ordinal)
            && string.Equals(left.Charge.Dimension, right.Charge.Dimension, StringComparison.Ordinal)
            && left.Charge.Amount == right.Charge.Amount
            && left.Limit == right.Limit
            && left.Consumed == right.Consumed
            && left.TriggeringReceiptId == right.TriggeringReceiptId
            && string.Equals(left.Reason, right.Reason, StringComparison.Ordinal)
            && left.RecordedAt == right.RecordedAt;
    }

    private static bool ResultReferenceEqual(DelegationResultReference? left, DelegationResultReference? right)
    {
        if (left is null || right is null) return left is null && right is null;
        return left.DelegationId == right.DelegationId
            && left.ResultId == right.ResultId
            && CandidateRevisionIdentity.SemanticallyEqual(left.Candidate, right.Candidate)
            && left.Artifacts.SequenceEqual(right.Artifacts)
            && EvidenceBundleIdentity.SemanticallyEqual(left.Evidence, right.Evidence);
    }

    private static void ValidateSnapshot(DelegationProgress progress)
    {
        EnsureDefined(progress.State);
        if (progress.Revision < 0)
        {
            throw new DelegationLifecycleViolationException("Progress revision cannot be negative.");
        }

        if (progress.WorkerCalls < 0 || progress.Retries < 0)
        {
            throw new DelegationLifecycleViolationException("Progress counters cannot be negative.");
        }

        if (progress.UpdatedAt == default)
        {
            throw new DelegationLifecycleViolationException("Progress must have an update timestamp.");
        }

        if (progress.State == DelegationState.WaitingForSupervisor && progress.Checkpoint is null)
        {
            throw new DelegationLifecycleViolationException(
                "WaitingForSupervisor progress must include a checkpoint descriptor.");
        }

        if (progress.State != DelegationState.WaitingForSupervisor && progress.Checkpoint is not null)
        {
            throw new DelegationLifecycleViolationException(
                "A checkpoint descriptor is valid only for WaitingForSupervisor progress.");
        }

        if (progress.Checkpoint is not null
            && (progress.Checkpoint.DelegationId != progress.DelegationId
                || progress.Checkpoint.ExpectedObservableRevision != progress.Revision))
        {
            throw new DelegationLifecycleViolationException(
                "A checkpoint must match its delegation and fence the enclosing progress revision exactly.");
        }
    }

    private static void EnsureCheckpointStable(
        SupervisorCheckpointDescriptor? previous,
        SupervisorCheckpointDescriptor? current)
    {
        if (previous is null || current is null || !CheckpointIdentityEqual(previous, current))
        {
            throw new DelegationLifecycleViolationException(
                "Waiting checkpoint identity cannot change while progress remains waiting; only its observable revision may advance.");
        }
    }

    private static void EnsureDefined(DelegationState state)
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown delegation state.");
        }
    }
}
