using System.Collections.Concurrent;

namespace Penghou.Qingniao.Codex;

/// <summary>
/// Process-isolated Codex CLI execution adapter. Each operation spawns one
/// `codex exec --json` process with an argv array (never a shell) inside an
/// approved workspace under an explicit sandbox. The Codex thread identity is
/// captured from the first `thread.started` event and handed to the seed sink
/// immediately, so losing the receipt never loses the thread: the same id
/// resumes later without duplicate work. Event shapes the adapter does not
/// model are counted, never interpreted.
/// </summary>
public sealed class CodexExecAdapter : IExternalOperationProvider
{
    private readonly CodexExecOptions options;
    private readonly ICodexProcessFactory processes;
    private readonly Func<DateTimeOffset> now;
    private readonly ConcurrentDictionary<string, TrackedOperation> operations = new(StringComparer.Ordinal);

    /// <summary>Creates an adapter for one Codex execution.</summary>
    public CodexExecAdapter(
        CodexExecOptions options,
        ICodexProcessFactory? processFactory = null,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
        processes = processFactory ?? new ProcessCodexProcessFactory();
        now = clock ?? (static () => DateTimeOffset.UtcNow);
    }

    /// <summary>Gets the last-spawned CLI invocation (diagnostics and tests).</summary>
    public CodexProcessInvocation? LastInvocation { get; private set; }

    /// <summary>Starts one Codex execution and captures its thread handle.</summary>
    public async ValueTask<ExternalOperationStartReceipt> StartAsync(
        ExternalOperationStartRequest request,
        IExternalOperationHandleCaptureSink handleSink,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(handleSink);
        var invocation = BuildInvocation(resumeThreadId: null);
        LastInvocation = invocation;
        ICodexProcess process;
        try
        {
            process = processes.Start(invocation);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new ExternalOperationProviderException(new ExternalOperationFailure(
                ExternalOperationFailureKind.Transport, "codex.spawn-failed",
                $"Codex process could not start: {exception.GetType().Name}.", retryable: false));
        }

        var tracked = new TrackedOperation(process, options.MaxOutputBytes);
        tracked.Pump = PumpAsync(tracked);
        try
        {
            var threadId = await WaitForThreadAsync(tracked, process, cancellationToken).ConfigureAwait(false);
            var handle = BuildHandle(request, threadId);
            tracked.Handle = handle;
            operations[threadId] = tracked;
            EvictCompleted();
            await handleSink.CaptureAsync(new ExternalOperationHandleCapture(handle, now()), cancellationToken)
                .ConfigureAwait(false);
            var state = tracked.Failure is null ? ExternalOperationState.Running : ExternalOperationState.Failed;
            return new ExternalOperationStartReceipt(
                request.Identity, handle, ExternalOperationStartDisposition.Created, state, now());
        }
        catch
        {
            try
            {
                process.Kill();
            }
            catch (InvalidOperationException)
            {
                // Already exited while failing.
            }

            tracked.Complete();
            throw;
        }
    }

    /// <summary>Observes one tracked Codex thread.</summary>
    public ValueTask<ExternalOperationObservation> ObserveAsync(
        ExternalOperationHandle handle,
        CancellationToken cancellationToken = default)
    {
        var tracked = RequireTracked(handle);
        var state = tracked.Completed
            ? tracked.TerminalState
            : ExternalOperationState.Running;
        var failure = state == ExternalOperationState.Failed ? tracked.Failure : null;
        var revision = System.Threading.Interlocked.Increment(ref tracked.ObservationsServed);
        return ValueTask.FromResult(new ExternalOperationObservation(
            handle, revision, state, now(), failure: failure, resultAvailable: tracked.Completed));
    }

    /// <summary>Returns the terminal result of one tracked Codex thread.</summary>
    public async ValueTask<ExternalOperationResult> GetResultAsync(
        ExternalOperationHandle handle,
        CancellationToken cancellationToken = default)
    {
        var tracked = RequireTracked(handle);
        await tracked.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (tracked.Cancelled)
        {
            throw new ExternalOperationProviderException(new ExternalOperationFailure(
                ExternalOperationFailureKind.Cancellation, "codex.cancelled",
                $"Codex thread '{tracked.ThreadId}' was cancelled.", retryable: false));
        }

        if (tracked.Failure is not null)
        {
            throw new ExternalOperationProviderException(tracked.Failure);
        }

        return new ExternalOperationResult(
            handle, ExternalOperationState.Succeeded, now(), tracked.Summary(), []);
    }

    /// <summary>Kills one tracked Codex process.</summary>
    public ValueTask<ExternalOperationCancellationReceipt> CancelAsync(
        ExternalOperationCancelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tracked = RequireTracked(request.Handle);
        tracked.Cancelled = true;
        try
        {
            tracked.Process.Kill();
        }
        catch (InvalidOperationException)
        {
            // Already exited between the lookup and the kill.
        }

        tracked.Complete();
        return ValueTask.FromResult(new ExternalOperationCancellationReceipt(
            request.Handle, request.CancellationKey,
            ExternalOperationCancellationDisposition.Requested,
            ExternalOperationState.CancellationRequested, now()));
    }

    /// <summary>
    /// Reconnects to a known Codex thread without starting duplicate work: no
    /// new prompt is sent, the same thread identity is tracked, and the
    /// receipt preserves the exact correlation. A fresh observer process is
    /// spawned only when this adapter is not already tracking the thread
    /// (for example after a host restart).
    /// </summary>
    public async ValueTask<ExternalOperationResumeReceipt> ResumeAsync(
        ExternalOperationResumeRequest request,
        IExternalOperationHandleCaptureSink handleSink,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(handleSink);
        var threadId = request.Handle.Value;
        if (!operations.ContainsKey(threadId))
        {
            var invocation = BuildInvocation(resumeThreadId: threadId);
            LastInvocation = invocation;
            ICodexProcess process;
            try
            {
                process = processes.Start(invocation);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new ExternalOperationProviderException(new ExternalOperationFailure(
                    ExternalOperationFailureKind.Transport, "codex.spawn-failed",
                    $"Codex process could not start: {exception.GetType().Name}.", retryable: false));
            }

            var tracked = new TrackedOperation(process, options.MaxOutputBytes) { ThreadId = threadId };
            tracked.ThreadFound.TrySetResult(threadId);
            operations[threadId] = tracked;
            tracked.Pump = PumpAsync(tracked);
            EvictCompleted();
        }

        await handleSink.CaptureAsync(
            new ExternalOperationHandleCapture(request.Handle, now()), cancellationToken).ConfigureAwait(false);
        return new ExternalOperationResumeReceipt(
            request.Handle, request.ResumeKey, request.Handle,
            ExternalOperationStartDisposition.Existing, ExternalOperationState.Running, now());
    }

    private CodexProcessInvocation BuildInvocation(string? resumeThreadId)
    {
        var arguments = new List<string> { "exec" };
        if (resumeThreadId is not null)
        {
            arguments.Add("resume");
            arguments.Add(resumeThreadId);
        }

        arguments.Add("--json");
        arguments.Add("-C");
        arguments.Add(options.WorkspaceDirectory);
        arguments.Add("-s");
        arguments.Add(options.Sandbox == CodexSandboxMode.ReadOnly ? "read-only" : "workspace-write");
        arguments.Add("--skip-git-repo-check");
        if (options.Ephemeral)
        {
            arguments.Add("--ephemeral");
        }

        if (options.Model is not null)
        {
            arguments.Add("-m");
            arguments.Add(options.Model);
        }

        if (options.OutputSchemaPath is not null)
        {
            arguments.Add("--output-schema");
            arguments.Add(options.OutputSchemaPath);
        }

        if (resumeThreadId is null)
        {
            arguments.Add(options.Prompt);
        }

        return new CodexProcessInvocation(options.CliPath, arguments, options.WorkspaceDirectory);
    }

    private ExternalOperationHandle BuildHandle(ExternalOperationStartRequest request, string threadId)
    {
        var agent = request.Correlation.Agent;
        var task = new ExternalTaskReference(agent.Provider, threadId);
        var correlation = new ExternalOperationCorrelation(
            request.Correlation.DelegationId,
            request.Correlation.WorkflowRun,
            request.Correlation.StructuralNode,
            request.Correlation.NodeGeneration,
            request.Correlation.ExecutionAttemptId,
            agent,
            task);
        return new ExternalOperationHandle(agent.Provider, threadId, agent.ProtocolVersion, correlation);
    }

    private static async Task<string> WaitForThreadAsync(
        TrackedOperation tracked,
        ICodexProcess process,
        CancellationToken cancellationToken)
    {
        var found = await Task.WhenAny(tracked.ThreadFound.Task, tracked.Completion.Task)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        if (found == tracked.ThreadFound.Task && !string.IsNullOrWhiteSpace(tracked.ThreadId))
        {
            return tracked.ThreadId;
        }

        try
        {
            process.Kill();
        }
        catch (InvalidOperationException)
        {
            // Already exited while failing.
        }

        throw new ExternalOperationProviderException(new ExternalOperationFailure(
            ExternalOperationFailureKind.Transport, "codex.no-thread",
            tracked.Failure is null
                ? "Codex ended before reporting a thread identity."
                : $"Codex ended before reporting a thread identity: {tracked.Failure.Summary}",
            retryable: false));
    }

    private async Task PumpAsync(TrackedOperation tracked)
    {
        try
        {
            await foreach (var line in tracked.Process.ReadLinesAsync().ConfigureAwait(false))
            {
                tracked.NoteLine(line);
                switch (CodexJsonlEvent.TryParse(line))
                {
                    case CodexJsonlEvent.ThreadStarted started
                        when !string.IsNullOrWhiteSpace(started.ThreadId):
                        if (string.IsNullOrEmpty(tracked.ThreadId))
                        {
                            tracked.ThreadId = started.ThreadId;
                            tracked.ThreadFound.TrySetResult(started.ThreadId);
                        }
                        else if (!string.Equals(tracked.ThreadId, started.ThreadId, StringComparison.Ordinal))
                        {
                            tracked.Fail(new ExternalOperationFailure(
                                ExternalOperationFailureKind.Remote, "codex.thread-changed",
                                "Codex reported a different thread identity mid-operation.", retryable: false));
                        }

                        break;
                    case CodexJsonlEvent.ErrorEvent error:
                        tracked.Fail(ClassifyError(error.Message));
                        break;
                    case CodexJsonlEvent.TurnFailed failed:
                        tracked.Fail(ClassifyError(failed.Message));
                        break;
                    case CodexJsonlEvent.Unknown:
                        tracked.UnknownEvents++;
                        break;
                    default:
                        break;
                }
            }

            var exit = await tracked.Process.WaitForExitAsync().ConfigureAwait(false);
            if (exit != 0 && tracked.Failure is null)
            {
                tracked.Fail(new ExternalOperationFailure(
                    ExternalOperationFailureKind.Remote, "codex.exit-failed",
                    $"Codex exited with code {exit} and no structured error.", retryable: false));
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            tracked.Fail(new ExternalOperationFailure(
                ExternalOperationFailureKind.Transport, "codex.pump-failed",
                $"Codex output pump failed: {exception.GetType().Name}.", retryable: false));
        }
        finally
        {
            tracked.ThreadFound.TrySetResult(tracked.ThreadId);
            tracked.Complete();
            await tracked.Process.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static ExternalOperationFailure ClassifyError(string message)
    {
        var summary = message.Length <= 1_024 ? message : message[..1_024];
        var usageLimited = summary.IndexOf("usage limit", StringComparison.OrdinalIgnoreCase) >= 0
            || summary.IndexOf("quota", StringComparison.OrdinalIgnoreCase) >= 0;
        return new ExternalOperationFailure(
            ExternalOperationFailureKind.Remote,
            usageLimited ? "codex.usage-limit" : "codex.remote-error",
            summary,
            retryable: false);
    }

    private TrackedOperation RequireTracked(ExternalOperationHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!operations.TryGetValue(handle.Value, out var tracked))
        {
            throw new InvalidOperationException($"Unknown Codex thread '{handle.Value}'.");
        }

        return tracked;
    }

    private void EvictCompleted()
    {
        const int capacity = 128;
        if (operations.Count <= capacity)
        {
            return;
        }

        foreach (var key in operations.Keys)
        {
            if (operations.Count <= capacity)
            {
                break;
            }

            if (operations.TryGetValue(key, out var tracked) && tracked.Completed)
            {
                operations.TryRemove(key, out _);
            }
        }
    }

    private sealed class TrackedOperation
    {
        private readonly long maxBytes;
        private long bytes;

        public TrackedOperation(ICodexProcess process, long maxOutputBytes)
        {
            Process = process;
            maxBytes = maxOutputBytes;
        }

        public ICodexProcess Process { get; }

        public Task? Pump { get; set; }

        public TaskCompletionSource<string> ThreadFound { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ThreadId { get; set; } = string.Empty;

        public ExternalOperationHandle? Handle { get; set; }

        public List<string> Transcript { get; } = [];

        public bool Truncated { get; private set; }

        public int UnknownEvents { get; set; }

        public long ObservationsServed;

        public ExternalOperationFailure? Failure { get; private set; }

        public bool Cancelled { get; set; }

        public bool Completed { get; private set; }

        public ExternalOperationState TerminalState =>
            Cancelled ? ExternalOperationState.Cancelled
            : Failure is not null ? ExternalOperationState.Failed
            : ExternalOperationState.Succeeded;

        public void Fail(ExternalOperationFailure failure) => Failure ??= failure;

        public void Complete()
        {
            Completed = true;
            Completion.TrySetResult();
        }

        public void NoteLine(string line)
        {
            // Keep draining past the budget so the child never blocks on a
            // full pipe; only capture is bounded.
            var size = System.Text.Encoding.UTF8.GetByteCount(line) + 1;
            if (bytes + size > maxBytes)
            {
                Truncated = true;
                return;
            }

            bytes += size;
            Transcript.Add(line);
        }

        public string Summary()
        {
            const int tail = 8;
            var kept = Transcript.Count <= tail ? Transcript : Transcript.GetRange(Transcript.Count - tail, tail);
            var text = string.Join("\n", kept);
            if (Truncated)
            {
                text += "\n[transcript truncated at the host byte budget]";
            }

            return text.Length <= 4_096 ? text : text[^4_096..];
        }
    }
}
