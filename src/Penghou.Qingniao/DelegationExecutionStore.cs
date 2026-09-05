namespace Penghou.Qingniao;

/// <summary>Classifies a rejected delegation execution-store write.</summary>
internal enum DelegationExecutionConflictKind
{
    /// <summary>The supplied snapshot belongs to another delegation.</summary>
    Ownership = 0,

    /// <summary>A delegation was created with a different initial snapshot.</summary>
    InitialSnapshot = 1,

    /// <summary>A terminal progress/result pair attempted to replace one already published.</summary>
    TerminalReplacement = 2,

    /// <summary>A terminal snapshot was supplied without its paired result.</summary>
    TerminalAtomicity = 3,

    /// <summary>A non-terminal progress update was supplied after a terminal snapshot.</summary>
    TerminalProgress = 4,
}

/// <summary>Raised when a delegation execution is not present in the store.</summary>
internal sealed class DelegationExecutionNotFoundException : KeyNotFoundException
{
    internal DelegationExecutionNotFoundException(DelegationId delegationId)
        : base($"Delegation '{delegationId}' was not found in the execution store.")
    {
        DelegationId = delegationId;
    }

    internal DelegationId DelegationId { get; }
}

/// <summary>Raised when an update carries a revision older than the stored snapshot.</summary>
internal sealed class DelegationExecutionStaleException : InvalidOperationException
{
    internal DelegationExecutionStaleException(
        DelegationId delegationId,
        long expectedRevision,
        long suppliedRevision)
        : base($"Delegation '{delegationId}' update revision {suppliedRevision} is stale; the current revision is {expectedRevision}.")
    {
        DelegationId = delegationId;
        ExpectedRevision = expectedRevision;
        SuppliedRevision = suppliedRevision;
    }

    internal DelegationId DelegationId { get; }
    internal long ExpectedRevision { get; }
    internal long SuppliedRevision { get; }
}

/// <summary>Raised when an execution-store write conflicts with immutable state.</summary>
internal sealed class DelegationExecutionConflictException : InvalidOperationException
{
    internal DelegationExecutionConflictException(
        DelegationId delegationId,
        DelegationExecutionConflictKind kind,
        string message)
        : base(message)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown execution-store conflict kind.");
        }

        DelegationId = delegationId;
        Kind = kind;
    }

    internal DelegationId DelegationId { get; }
    internal DelegationExecutionConflictKind Kind { get; }
}

/// <summary>Immutable point-in-time state of one delegation execution.</summary>
internal sealed class DelegationExecutionSnapshot
{
    internal DelegationExecutionSnapshot(DelegationProgress progress, DelegationResult? result)
    {
        ArgumentNullException.ThrowIfNull(progress);
        DelegationLifecycle.ValidateResultAvailability(progress, result);
        Progress = progress;
        Result = result;
    }

    internal DelegationProgress Progress { get; }
    internal DelegationResult? Result { get; }
}

/// <summary>
/// Bounded, thread-safe in-memory execution aggregate. The store only records
/// accepted snapshots; it never schedules or executes delegation work.
/// </summary>
internal sealed class InMemoryDelegationExecutionStore
{
    internal const int MaximumEntries = 256;

    private readonly object _gate = new();
    private readonly int _maximumEntries;
    private readonly Dictionary<DelegationId, DelegationExecutionSnapshot> _entries = new();

    internal InMemoryDelegationExecutionStore()
        : this(MaximumEntries)
    {
    }

    internal InMemoryDelegationExecutionStore(int maximumEntries)
    {
        if (maximumEntries is < 1 or > MaximumEntries)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumEntries),
                maximumEntries,
                $"The maximum entry count must be between 1 and {MaximumEntries}.");
        }

        _maximumEntries = maximumEntries;
    }

    internal int Capacity => _maximumEntries;

    /// <summary>Creates an execution at revision zero in the Queued state.</summary>
    internal ValueTask<DelegationExecutionSnapshot> CreateAsync(
        DelegationId delegationId,
        DateTimeOffset queuedAt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var queued = new DelegationProgress(
            delegationId,
            DelegationState.Queued,
            revision: 0,
            currentSteps: [],
            completedSteps: [],
            workerCalls: 0,
            retries: 0,
            queuedAt);
        return CreateAsync(queued, cancellationToken);
    }

    /// <summary>
    /// Creates an execution from its required Queued revision-zero snapshot.
    /// Repeating the exact initial snapshot is idempotent.
    /// </summary>
    internal ValueTask<DelegationExecutionSnapshot> CreateAsync(
        DelegationProgress queued,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(queued);
        DelegationLifecycle.ValidateProgress(queued);
        if (queued.State != DelegationState.Queued || queued.Revision != 0)
        {
            throw new DelegationExecutionConflictException(
                queued.DelegationId,
                DelegationExecutionConflictKind.InitialSnapshot,
                "An execution must be created with a Queued progress snapshot at revision zero.");
        }

        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_entries.TryGetValue(queued.DelegationId, out var existing))
            {
                if (ProgressEqual(existing.Progress, queued) && existing.Result is null)
                {
                    return ValueTask.FromResult(existing);
                }

                throw new DelegationExecutionConflictException(
                    queued.DelegationId,
                    DelegationExecutionConflictKind.InitialSnapshot,
                    "The delegation already has a different execution snapshot.");
            }

            if (_entries.Count >= _maximumEntries)
            {
                throw new InvalidOperationException(
                    $"A delegation execution store cannot contain more than {_maximumEntries} entries.");
            }

            var snapshot = new DelegationExecutionSnapshot(queued, null);
            _entries.Add(queued.DelegationId, snapshot);
            return ValueTask.FromResult(snapshot);
        }
    }

    /// <summary>Gets one immutable execution snapshot.</summary>
    internal ValueTask<DelegationExecutionSnapshot> GetAsync(
        DelegationId delegationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(GetRequired(delegationId));
        }
    }

    /// <summary>Publishes a non-terminal progress revision using an atomic fence.</summary>
    internal ValueTask<DelegationExecutionSnapshot> PublishProgressAsync(
        DelegationId delegationId,
        DelegationProgress progress,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(progress);
        EnsureOwnership(delegationId, progress.DelegationId);
        DelegationLifecycle.ValidateProgress(progress);

        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existing = GetRequired(delegationId);
            if (progress.Revision < existing.Progress.Revision)
            {
                throw new DelegationExecutionStaleException(
                    delegationId,
                    existing.Progress.Revision,
                    progress.Revision);
            }

            if (DelegationLifecycle.IsTerminal(progress.State))
            {
                throw Conflict(
                    delegationId,
                    DelegationExecutionConflictKind.TerminalAtomicity,
                    "Terminal progress must be published atomically with its result.");
            }

            if (progress.Revision == existing.Progress.Revision)
            {
                if (ProgressEqual(existing.Progress, progress))
                {
                    return ValueTask.FromResult(existing);
                }

                throw Conflict(
                    delegationId,
                    DelegationExecutionConflictKind.InitialSnapshot,
                    "A changed progress snapshot cannot reuse an existing revision.");
            }

            if (DelegationLifecycle.IsTerminal(existing.Progress.State))
            {
                throw Conflict(
                    delegationId,
                    DelegationExecutionConflictKind.TerminalProgress,
                    "Terminal progress cannot be changed after publication.");
            }

            DelegationLifecycle.ValidateProgress(progress, existing.Progress);
            var snapshot = new DelegationExecutionSnapshot(progress, null);
            _entries[delegationId] = snapshot;
            return ValueTask.FromResult(snapshot);
        }
    }

    /// <summary>
    /// Atomically publishes a terminal progress/result pair. An exact pair
    /// replay returns the original snapshot; no terminal state is replaceable.
    /// </summary>
    internal ValueTask<DelegationExecutionSnapshot> PublishTerminalAsync(
        DelegationId delegationId,
        DelegationProgress progress,
        DelegationResult result,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(result);
        EnsureOwnership(delegationId, progress.DelegationId);
        EnsureOwnership(delegationId, result.DelegationId);
        DelegationLifecycle.ValidateProgress(progress);
        DelegationLifecycle.ValidateResult(result);
        if (!DelegationLifecycle.IsTerminal(progress.State)
            || progress.State != result.State)
        {
            throw Conflict(
                delegationId,
                DelegationExecutionConflictKind.TerminalAtomicity,
                "Terminal publication requires matching terminal progress and result.");
        }

        DelegationLifecycle.ValidateResultAvailability(progress, result);

        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existing = GetRequired(delegationId);
            if (progress.Revision < existing.Progress.Revision)
            {
                throw new DelegationExecutionStaleException(
                    delegationId,
                    existing.Progress.Revision,
                    progress.Revision);
            }

            if (progress.Revision == existing.Progress.Revision)
            {
                if (ProgressEqual(existing.Progress, progress)
                    && ResultEqual(existing.Result, result))
                {
                    return ValueTask.FromResult(existing);
                }

                throw Conflict(
                    delegationId,
                    DelegationExecutionConflictKind.TerminalReplacement,
                    "A terminal progress/result pair cannot replace existing state at the same revision.");
            }

            if (existing.Result is not null || DelegationLifecycle.IsTerminal(existing.Progress.State))
            {
                throw Conflict(
                    delegationId,
                    DelegationExecutionConflictKind.TerminalReplacement,
                    "A terminal progress/result pair cannot replace an already terminal execution.");
            }

            DelegationLifecycle.ValidateProgress(progress, existing.Progress);
            var snapshot = new DelegationExecutionSnapshot(progress, result);
            _entries[delegationId] = snapshot;
            return ValueTask.FromResult(snapshot);
        }
    }

    /// <summary>Returns all executions in deterministic delegation-id order.</summary>
    internal IReadOnlyList<DelegationExecutionSnapshot> List()
    {
        lock (_gate)
        {
            var copy = _entries
                .OrderBy(entry => entry.Key.Value.ToString("D"), StringComparer.Ordinal)
                .Select(entry => entry.Value)
                .ToArray();
            return Array.AsReadOnly(copy);
        }
    }

    private DelegationExecutionSnapshot GetRequired(DelegationId delegationId) =>
        _entries.TryGetValue(delegationId, out var snapshot)
            ? snapshot
            : throw new DelegationExecutionNotFoundException(delegationId);

    private static void EnsureOwnership(DelegationId expected, DelegationId supplied)
    {
        if (expected != supplied)
        {
            throw Conflict(
                expected,
                DelegationExecutionConflictKind.Ownership,
                "A progress or result snapshot belongs to a different delegation.");
        }
    }

    private static DelegationExecutionConflictException Conflict(
        DelegationId delegationId,
        DelegationExecutionConflictKind kind,
        string message) => new(delegationId, kind, message);

    private static bool ProgressEqual(DelegationProgress left, DelegationProgress right) =>
        left.DelegationId == right.DelegationId
        && left.State == right.State
        && left.Revision == right.Revision
        && left.WorkerCalls == right.WorkerCalls
        && left.Retries == right.Retries
        && left.UpdatedAt == right.UpdatedAt
        && left.CurrentSteps.SequenceEqual(right.CurrentSteps, StringComparer.Ordinal)
        && left.CompletedSteps.SequenceEqual(right.CompletedSteps, StringComparer.Ordinal)
        && CheckpointEqual(left.Checkpoint, right.Checkpoint);

    private static bool CheckpointEqual(
        SupervisorCheckpointDescriptor? left,
        SupervisorCheckpointDescriptor? right)
    {
        if (left is null || right is null) return left is null && right is null;
        return left.CheckpointId == right.CheckpointId
            && left.Session == right.Session
            && left.DelegationId == right.DelegationId
            && left.PlanRevision == right.PlanRevision
            && left.WorkflowRun == right.WorkflowRun
            && left.StructuralNode == right.StructuralNode
            && left.NodeGeneration == right.NodeGeneration
            && left.ExpectedObservableRevision == right.ExpectedObservableRevision
            && left.DependentProgressGated == right.DependentProgressGated;
    }

    private static bool ResultEqual(DelegationResult? left, DelegationResult? right)
    {
        if (left is null || right is null) return left is null && right is null;
        return left.DelegationId == right.DelegationId
            && left.State == right.State
            && string.Equals(left.Summary, right.Summary, StringComparison.Ordinal)
            && left.Evidence.TestsPassed == right.Evidence.TestsPassed
            && left.Evidence.TestsFailed == right.Evidence.TestsFailed
            && left.Evidence.ReviewApproved == right.Evidence.ReviewApproved
            && left.Evidence.ReviewFindingsResolved == right.Evidence.ReviewFindingsResolved
            && left.Evidence.ChangedFiles.SequenceEqual(right.Evidence.ChangedFiles, StringComparer.Ordinal)
            && left.Evidence.Commands.SequenceEqual(right.Evidence.Commands, StringComparer.Ordinal)
            && EvidenceBundleIdentity.SemanticallyEqual(left.NormalizedEvidence, right.NormalizedEvidence)
            && BudgetEqual(left.BudgetExceeded, right.BudgetExceeded)
            && left.Artifacts.SequenceEqual(right.Artifacts)
            && left.UnresolvedConcerns.SequenceEqual(right.UnresolvedConcerns, StringComparer.Ordinal)
            && left.CompletedAt == right.CompletedAt;
    }

    private static bool BudgetEqual(BudgetExceededOutcome? left, BudgetExceededOutcome? right)
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
}
