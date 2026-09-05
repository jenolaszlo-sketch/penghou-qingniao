namespace Penghou.Qingniao;

/// <summary>Identifies the safe metadata for a handle-capture conflict.</summary>
public enum ExternalOperationHandleCaptureConflictKind
{
    /// <summary>The stable execution identity already has another handle.</summary>
    ExecutionIdentity = 0,

    /// <summary>The provider-issued handle is already owned by another execution.</summary>
    ReusedHandle = 1,
}

/// <summary>
/// Raised when an external-operation handle capture would bind one stable
/// operation or provider handle to conflicting immutable data. Handle values
/// are intentionally never retained by this exception or included in its
/// message; providers must use opaque, non-secret identifiers.
/// </summary>
public sealed class ExternalOperationHandleCaptureConflictException : InvalidOperationException
{
    /// <summary>Initializes a conflict with safe conflict metadata.</summary>
    public ExternalOperationHandleCaptureConflictException(
        ExternalOperationHandleCaptureConflictKind kind)
        : base(CreateMessage(kind))
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown handle-capture conflict kind.");
        }

        Kind = kind;
    }

    /// <summary>Gets the safe conflict classification.</summary>
    public ExternalOperationHandleCaptureConflictKind Kind { get; }

    private static string CreateMessage(ExternalOperationHandleCaptureConflictKind kind) => kind switch
    {
        ExternalOperationHandleCaptureConflictKind.ExecutionIdentity => "The stable external-operation execution identity is already bound to a different capture.",
        ExternalOperationHandleCaptureConflictKind.ReusedHandle => "The provider-issued external-operation handle is already bound to another execution.",
        _ => "The external-operation handle capture conflicted with an existing binding.",
    };
}

/// <summary>Immutable point-in-time view of captured external-operation handles.</summary>
public sealed class ExternalOperationHandleCaptureRegistrySnapshot
{
    internal ExternalOperationHandleCaptureRegistrySnapshot(
        IReadOnlyList<ExternalOperationHandleCapture> captures)
    {
        Captures = captures;
    }

    /// <summary>Gets all captured handles in the snapshot.</summary>
    public IReadOnlyList<ExternalOperationHandleCapture> Captures { get; }

    /// <summary>Gets the number of captured handles in the snapshot.</summary>
    public int Count => Captures.Count;

    /// <summary>Looks up a capture by its stable execution correlation and attempt.</summary>
    public bool TryGet(
        ExternalOperationCorrelation correlation,
        out ExternalOperationHandleCapture? capture)
    {
        var key = ExternalOperationExecutionKey.Create(correlation);
        foreach (var candidate in Captures)
        {
            if (ExternalOperationExecutionKey.Create(candidate.Handle.Correlation) == key)
            {
                capture = candidate;
                return true;
            }
        }

        capture = null;
        return false;
    }

    /// <summary>Looks up a capture by its provider-issued handle.</summary>
    public bool TryGet(
        ExternalOperationHandle handle,
        out ExternalOperationHandleCapture? capture)
    {
        ArgumentNullException.ThrowIfNull(handle);
        handle.Validate();
        var key = new ExternalOperationHandleKey(handle.Provider, handle.Value);
        foreach (var candidate in Captures)
        {
            if (new ExternalOperationHandleKey(candidate.Handle.Provider, candidate.Handle.Value) == key)
            {
                capture = candidate;
                return true;
            }
        }

        capture = null;
        return false;
    }
}

/// <summary>
/// Thread-safe, bounded in-memory sink for early external-operation handle
/// captures. Replays of the same operation and handle are idempotent while
/// retaining the earliest capture timestamp. The registry is intentionally
/// scoped to one bounded delegation/workflow-run/session lifecycle; hosts
/// must create a new instance per lifecycle rather than treating it as a
/// long-lived global store. Handle values are opaque, non-secret identifiers;
/// snapshots must be protected or redacted according to host policy.
/// </summary>
public sealed class InMemoryExternalOperationHandleCaptureRegistry : IExternalOperationHandleCaptureSink
{
    /// <summary>The largest capacity accepted by this bounded registry.</summary>
    public const int MaximumEntries = 256;

    private readonly object _gate = new();
    private readonly int _maximumEntries;
    private readonly Dictionary<ExternalOperationExecutionKey, ExternalOperationHandleCapture> _byExecution = new();
    private readonly Dictionary<ExternalOperationHandleKey, ExternalOperationHandleCapture> _byHandle = new();

    /// <summary>Initializes an empty registry with the default maximum capacity.</summary>
    public InMemoryExternalOperationHandleCaptureRegistry()
        : this(MaximumEntries)
    {
    }

    /// <summary>Initializes an empty registry with a bounded maximum capacity.</summary>
    /// <param name="maximumEntries">Maximum number of distinct captures retained.</param>
    public InMemoryExternalOperationHandleCaptureRegistry(int maximumEntries)
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

    /// <summary>Gets the configured maximum number of retained captures.</summary>
    public int Capacity => _maximumEntries;

    /// <summary>
    /// Captures a provider-issued handle atomically. A replay with the same
    /// operation and handle succeeds and retains the earliest timestamp; any
    /// conflicting operation or handle binding is rejected.
    /// </summary>
    /// <exception cref="ExternalOperationHandleCaptureConflictException">
    /// Thrown when the execution identity or provider handle is already bound
    /// to a different capture.
    /// </exception>
    public ValueTask CaptureAsync(
        ExternalOperationHandleCapture capture,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(capture);
        capture.Handle.Validate();

        var executionKey = ExternalOperationExecutionKey.Create(capture.Handle.Correlation);
        var handleKey = new ExternalOperationHandleKey(capture.Handle.Provider, capture.Handle.Value);

        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_byExecution.TryGetValue(executionKey, out var existingByExecution))
            {
                if (HandlesEqual(existingByExecution.Handle, capture.Handle))
                {
                    if (capture.CapturedAt < existingByExecution.CapturedAt)
                    {
                        _byExecution[executionKey] = capture;
                        _byHandle[handleKey] = capture;
                    }

                    return ValueTask.CompletedTask;
                }

                throw Conflict(ExternalOperationHandleCaptureConflictKind.ExecutionIdentity);
            }

            if (_byHandle.ContainsKey(handleKey))
            {
                throw Conflict(ExternalOperationHandleCaptureConflictKind.ReusedHandle);
            }

            if (_byExecution.Count >= _maximumEntries)
            {
                throw new InvalidOperationException(
                    $"An external-operation handle capture registry cannot contain more than {_maximumEntries} entries.");
            }

            _byExecution.Add(executionKey, capture);
            _byHandle.Add(handleKey, capture);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Looks up a capture by its stable execution correlation and attempt.</summary>
    public bool TryGet(
        ExternalOperationCorrelation correlation,
        out ExternalOperationHandleCapture? capture)
    {
        var key = ExternalOperationExecutionKey.Create(correlation);
        lock (_gate)
        {
            return _byExecution.TryGetValue(key, out capture);
        }
    }

    /// <summary>Looks up a capture by its provider-issued handle.</summary>
    public bool TryGet(
        ExternalOperationHandle handle,
        out ExternalOperationHandleCapture? capture)
    {
        ArgumentNullException.ThrowIfNull(handle);
        handle.Validate();
        var key = new ExternalOperationHandleKey(handle.Provider, handle.Value);
        lock (_gate)
        {
            return _byHandle.TryGetValue(key, out capture);
        }
    }

    /// <summary>Captures an immutable point-in-time view for reconnect or replay.</summary>
    public ExternalOperationHandleCaptureRegistrySnapshot GetSnapshot()
    {
        lock (_gate)
        {
            var copy = _byExecution.Values
                .OrderBy(capture => ExternalOperationExecutionKey.Create(capture.Handle.Correlation).SortKey, StringComparer.Ordinal)
                .ToArray();
            return new ExternalOperationHandleCaptureRegistrySnapshot(Array.AsReadOnly(copy));
        }
    }

    private static bool HandlesEqual(ExternalOperationHandle left, ExternalOperationHandle right) =>
        string.Equals(left.Provider, right.Provider, StringComparison.Ordinal)
        && string.Equals(left.Value, right.Value, StringComparison.Ordinal)
        && string.Equals(left.ProtocolVersion, right.ProtocolVersion, StringComparison.Ordinal)
        && ExternalOperationExecutionKey.Create(left.Correlation) == ExternalOperationExecutionKey.Create(right.Correlation)
        && string.Equals(left.Correlation.Task!.Provider, right.Correlation.Task!.Provider, StringComparison.Ordinal)
        && string.Equals(left.Correlation.Task.Identifier, right.Correlation.Task.Identifier, StringComparison.Ordinal);

    private static ExternalOperationHandleCaptureConflictException Conflict(
        ExternalOperationHandleCaptureConflictKind kind) => new(kind);

}

internal readonly record struct ExternalOperationHandleKey(string Provider, string Value);

internal readonly record struct ExternalOperationExecutionKey(
    DelegationId DelegationId,
    string WorkflowProvider,
    string WorkflowRunId,
    string ExecutionEpoch,
    string StructuralNode,
    Guid NodeGeneration,
    string ExecutionAttemptId,
    string AgentProvider,
    string AgentIdentifier,
    string ProtocolVersion)
{
    public string SortKey => string.Join(
        '\u001f',
        DelegationId.Value.ToString("D"),
        WorkflowProvider,
        WorkflowRunId,
        ExecutionEpoch,
        StructuralNode,
        NodeGeneration.ToString("D"),
        ExecutionAttemptId,
        AgentProvider,
        AgentIdentifier,
        ProtocolVersion);

    public static ExternalOperationExecutionKey Create(ExternalOperationCorrelation correlation)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        correlation.Validate();
        return new ExternalOperationExecutionKey(
            correlation.DelegationId,
            correlation.WorkflowRun.Provider,
            correlation.WorkflowRun.WorkflowRunId,
            correlation.WorkflowRun.ExecutionEpoch,
            correlation.StructuralNode.Identifier,
            correlation.NodeGeneration.Value,
            correlation.ExecutionAttemptId,
            correlation.Agent.Provider,
            correlation.Agent.Identifier,
            correlation.Agent.ProtocolVersion);
    }
}
