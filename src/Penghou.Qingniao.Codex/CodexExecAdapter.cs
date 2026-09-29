using System.Collections.Concurrent;

namespace Penghou.Qingniao.Codex;

/// <summary>Bounded per-stream I/O counters for one tracked Codex thread.</summary>
/// <param name="StdoutLines">Retained stdout lines.</param>
/// <param name="StdoutBytes">Retained stdout bytes.</param>
/// <param name="StdoutTruncated">Whether stdout capture hit its budget.</param>
/// <param name="StderrLines">Retained stderr lines, classified separately from stdout.</param>
/// <param name="StderrBytes">Retained stderr bytes.</param>
/// <param name="StderrTruncated">Whether stderr capture hit its budget.</param>
internal sealed record CodexOperationIODiagnostics(
    int StdoutLines,
    long StdoutBytes,
    bool StdoutTruncated,
    int StderrLines,
    long StderrBytes,
    bool StderrTruncated);

/// <summary>
/// Process-isolated Codex CLI execution adapter. Each operation spawns one
/// `codex exec --json` process with an argv array (never a shell) inside an
/// approved workspace under an explicit sandbox. The Codex thread identity is
/// captured from the first `thread.started` event and handed to the seed sink
/// immediately, so losing the receipt never loses the thread: the same id
/// resumes later without duplicate work. Event shapes the adapter does not
/// model are counted, never interpreted.
/// </summary>
/// <remarks>
/// This adapter is explicitly single-use per configured prompt: every spawn
/// uses the prompt (and workspace) from its options, so one instance serves
/// exactly one admitted objective. Hosts needing per-request prompts must
/// either construct one adapter per admitted objective or front a
/// host-owned resolving adapter; the coordinator's materialized input
/// artifacts are opaque to this adapter and never reinterpreted here.
/// Verified completion requires a completed turn: a clean process exit
/// without turn evidence fails closed instead of claiming success. Terminal
/// time, state, output and provenance freeze once, so repeated result reads
/// are identical; acknowledged receipts are evicted before unread ones, and
/// unread overflow past capacity is counted, never silent.
/// </remarks>
public sealed class CodexExecAdapter : IExternalOperationProvider
{
    private readonly CodexExecOptions options;
    private readonly ICodexProcessFactory processes;
    private readonly Func<DateTimeOffset> now;
    private readonly ConcurrentDictionary<string, TrackedOperation> operations = new(StringComparer.Ordinal);
    private readonly object invocationLock = new();
    private CodexProcessInvocation? lastInvocation;
    private long evictedUnreadReceipts;
    private long startSequence;

    // Single-flight launch slots, one lock for both maps: a start or resume
    // publishes its slot before any process launch, so concurrent equivalent
    // requests attach instead of spawning twice. No awaits run inside.
    private readonly object launchGate = new();
    private readonly Dictionary<string, InFlightStart> inFlightStarts = new(StringComparer.Ordinal);

    // Idempotency-key bindings for launched operations: a retried start with
    // the same key and semantics attaches to the tracked thread instead of
    // spawning again. Entries live only while their thread is tracked;
    // eviction prunes them together. Durable cross-restart identity stays
    // with the coordinator above this adapter.
    private readonly Dictionary<string, StartBinding> startBindings = new(StringComparer.Ordinal);

    private sealed record StartBinding(ExternalOperationStartIdentity Identity, string ThreadId);

    private sealed class InFlightStart
    {
        public required ExternalOperationStartIdentity Identity;

        public TaskCompletionSource<TrackedOperation> Slot { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

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
    public CodexProcessInvocation? LastInvocation
    {
        get
        {
            lock (invocationLock)
            {
                return lastInvocation;
            }
        }

        private set
        {
            lock (invocationLock)
            {
                lastInvocation = value;
            }
        }
    }

    internal int TrackedOperationCount => operations.Count;

    /// <summary>Reads bounded per-stream I/O counters for one tracked thread (diagnostics and tests).</summary>
    internal CodexOperationIODiagnostics GetIODiagnostics(ExternalOperationHandle handle)
    {
        var tracked = RequireTracked(handle);
        return new CodexOperationIODiagnostics(
            tracked.Transcript.Count,
            tracked.TranscriptBytes,
            tracked.Truncated,
            tracked.StderrTranscript.Count,
            tracked.StderrTranscriptBytes,
            tracked.StderrTruncated);
    }

    /// <summary>
    /// Starts one Codex execution and captures its thread handle. An
    /// already-cancelled call spawns nothing. Concurrent starts with the same
    /// idempotency key attach to the first launch; the same key with different
    /// start semantics is a conflict.
    /// </summary>
    public async ValueTask<ExternalOperationStartReceipt> StartAsync(
        ExternalOperationStartRequest request,
        IExternalOperationHandleCaptureSink handleSink,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(handleSink);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWorkspaceForLaunch();

        // A retried start for a still-tracked launch attaches by key instead
        // of spawning duplicate provider work.
        TrackedOperation? bound;
        lock (launchGate)
        {
            if (startBindings.TryGetValue(request.Identity.IdempotencyKey, out var binding)
                && operations.TryGetValue(binding.ThreadId, out bound))
            {
                if (!binding.Identity.Equals(request.Identity))
                {
                    bound = null;
                    throw new ExternalOperationProviderException(new ExternalOperationFailure(
                        ExternalOperationFailureKind.Rejection, "codex.start-conflict",
                        "The start idempotency key is already bound to different start semantics.",
                        retryable: false));
                }
            }
            else
            {
                bound = null;
                startBindings.Remove(request.Identity.IdempotencyKey);
            }
        }

        if (bound is not null)
        {
            return await CaptureAttachedAsync(request, bound, handleSink, cancellationToken).ConfigureAwait(false);
        }

        InFlightStart slot;
        var owner = false;
        lock (launchGate)
        {
            if (inFlightStarts.TryGetValue(request.Identity.IdempotencyKey, out var existing))
            {
                if (!existing.Identity.Equals(request.Identity))
                {
                    throw new ExternalOperationProviderException(new ExternalOperationFailure(
                        ExternalOperationFailureKind.Rejection, "codex.start-conflict",
                        "The start idempotency key is already bound to different start semantics.",
                        retryable: false));
                }

                slot = existing;
            }
            else
            {
                slot = new InFlightStart { Identity = request.Identity };
                inFlightStarts[request.Identity.IdempotencyKey] = slot;
                owner = true;
            }
        }

        if (!owner)
        {
            // Attach to the in-flight launch instead of spawning duplicate work.
            var attached = await slot.Slot.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await CaptureAttachedAsync(request, attached, handleSink, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var tracked = await RunStartAsync(request, slot).ConfigureAwait(false);
            await handleSink.CaptureAsync(
                new ExternalOperationHandleCapture(tracked.ExpectedHandle!, now()), cancellationToken)
                .ConfigureAwait(false);
            // Project the same terminal state ObserveAsync will report: a fast
            // run may already be terminal (including Transport-kind Unknown).
            var state = tracked.Completed ? tracked.ProjectedState : ExternalOperationState.Running;
            return new ExternalOperationStartReceipt(
                request.Identity, tracked.ExpectedHandle!, ExternalOperationStartDisposition.Created, state, now());
        }
        catch (Exception exception)
        {
            // RunStartAsync publishes known launch failures itself. Cover any
            // earlier or unexpected failure before an in-flight slot is bound.
            slot.Slot.TrySetException(exception);
            throw;
        }
        finally
        {
            lock (launchGate)
            {
                inFlightStarts.Remove(request.Identity.IdempotencyKey);
            }
        }
    }

    private async ValueTask<ExternalOperationStartReceipt> CaptureAttachedAsync(
        ExternalOperationStartRequest request,
        TrackedOperation tracked,
        IExternalOperationHandleCaptureSink handleSink,
        CancellationToken cancellationToken)
    {
        var attachedHandle = tracked.ExpectedHandle;
        if (attachedHandle is null)
        {
            throw new InvalidOperationException("The shared start completed without a bound handle.");
        }

        await handleSink.CaptureAsync(
            new ExternalOperationHandleCapture(attachedHandle, now()), cancellationToken).ConfigureAwait(false);
        var state = tracked.Completed ? tracked.ProjectedState : ExternalOperationState.Running;
        return new ExternalOperationStartReceipt(
            request.Identity, attachedHandle, ExternalOperationStartDisposition.Existing, state, now());
    }

    private async Task<TrackedOperation> RunStartAsync(ExternalOperationStartRequest request, InFlightStart slot)
    {
        var invocation = BuildInvocation(resumeThreadId: null);
        LastInvocation = invocation;
        ICodexProcess process;
        try
        {
            process = processes.Start(invocation);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var failure = new ExternalOperationProviderException(new ExternalOperationFailure(
                ExternalOperationFailureKind.Transport, "codex.spawn-failed",
                $"Codex process could not start: {exception.GetType().Name}.", retryable: false));
            slot.Slot.TrySetException(failure);
            throw failure;
        }

        var tracked = new TrackedOperation(process, options.MaxOutputBytes);
        tracked.Pump = PumpAsync(tracked);
        try
        {
            var threadId = await WaitForThreadAsync(tracked, process, request.Deadline, CancellationToken.None).ConfigureAwait(false);
            var handle = BuildHandle(request, threadId);
            tracked.BindHandle(handle);
            if (!operations.TryAdd(threadId, tracked))
            {
                try
                {
                    process.Kill();
                }
                catch (InvalidOperationException)
                {
                    // Already exited while failing.
                }

                throw new InvalidOperationException($"Codex thread '{threadId}' is already tracked.");
            }

            lock (launchGate)
            {
                tracked.Sequence = System.Threading.Interlocked.Increment(ref startSequence);
                startBindings[request.Identity.IdempotencyKey] = new StartBinding(request.Identity, threadId);
            }

            EvictCompleted();
            slot.Slot.TrySetResult(tracked);
            return tracked;
        }
        catch (Exception exception)
        {
            // Attach concurrent starters to the real outcome; the pump keeps
            // ownership of the terminal transition below.
            slot.Slot.TrySetException(exception);
            try
            {
                process.Kill();
            }
            catch (InvalidOperationException)
            {
                // Already exited while failing.
            }

            // Terminal outcome stays with the pump, which owns exit/drain
            // coordination; this path only releases attachers and rethrows.
            throw;
        }
    }

    /// <summary>Re-validates the approved workspace immediately before a launch (QH-02 hook).</summary>
    private void EnsureWorkspaceForLaunch()
    {
        try
        {
            options.ValidateWorkspaceBeforeLaunch();
        }
        catch (UnauthorizedAccessException)
        {
            throw new ExternalOperationProviderException(new ExternalOperationFailure(
                ExternalOperationFailureKind.Transport, "codex.workspace-refused",
                "The approved workspace is no longer contained; launch was refused.",
                retryable: false));
        }
    }

    /// <summary>Observes one tracked Codex thread.</summary>
    public ValueTask<ExternalOperationObservation> ObserveAsync(
        ExternalOperationHandle handle,
        CancellationToken cancellationToken = default)
    {
        var tracked = RequireTracked(handle);
        var completed = tracked.Completed;
        var state = completed
            ? tracked.ProjectedState
            : ExternalOperationState.Running;
        var failure = state == ExternalOperationState.Failed || state == ExternalOperationState.Unknown
            ? tracked.Failure
            : null;
        var revision = System.Threading.Interlocked.Increment(ref tracked.ObservationsServed);
        // Unknown terminals carry no retrievable result under the frozen
        // observation contract, even though the operation finished.
        var resultAvailable = completed && state != ExternalOperationState.Unknown;
        return ValueTask.FromResult(new ExternalOperationObservation(
            handle, revision, state, now(), failure: failure, resultAvailable: resultAvailable));
    }

    /// <summary>
    /// Returns the terminal result of one tracked Codex thread. Time, state
    /// and output come from the frozen terminal receipt, so repeated reads
    /// are identical. Every terminal read acknowledges the receipt for the
    /// retention policy.
    /// </summary>
    public async ValueTask<ExternalOperationResult> GetResultAsync(
        ExternalOperationHandle handle,
        CancellationToken cancellationToken = default)
    {
        var tracked = RequireTracked(handle);
        await tracked.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        tracked.ResultRetrieved = true;
        var receipt = tracked.TerminalReceipt
            ?? throw new InvalidOperationException($"Codex thread '{tracked.ThreadId}' completed without a frozen receipt.");
        switch (receipt.State)
        {
            case ExternalOperationState.Cancelled:
                throw new ExternalOperationProviderException(new ExternalOperationFailure(
                    ExternalOperationFailureKind.Cancellation, "codex.cancelled",
                    $"Codex thread '{tracked.ThreadId}' was cancelled.", retryable: false));
            case ExternalOperationState.Failed:
            case ExternalOperationState.Unknown:
                throw new ExternalOperationProviderException(tracked.Failure
                    ?? throw new InvalidOperationException($"Codex thread '{tracked.ThreadId}' failed without recorded evidence."));
            case ExternalOperationState.Succeeded:
                return new ExternalOperationResult(
                    handle, ExternalOperationState.Succeeded, receipt.CompletedAt, receipt.Summary, []);
            default:
                throw new InvalidOperationException($"Codex thread '{tracked.ThreadId}' completed without an outcome.");
        }
    }

    /// <summary>Reads the frozen terminal receipt, or null while running (diagnostics and tests).</summary>
    internal CodexTerminalReceipt? GetTerminalReceipt(ExternalOperationHandle handle) =>
        RequireTracked(handle).TerminalReceipt;

    /// <summary>
    /// Records cancellation intent for one tracked Codex thread and waits for
    /// the confirmed terminal outcome. The intent is separate from the
    /// confirmation: a late cancellation preserves success or failure and
    /// reports
    /// <see cref="ExternalOperationCancellationDisposition.AlreadyTerminal"/>.
    /// Repeating a key replays its receipt; a different key conflicts.
    /// </summary>
    public async ValueTask<ExternalOperationCancellationReceipt> CancelAsync(
        ExternalOperationCancelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tracked = RequireTracked(request.Handle);
        var receipt = tracked.RequestCancel(request.CancellationKey, request.Handle, now());
        // Confirm before returning: when this completes, observation already
        // reflects the terminal outcome instead of racing the pump.
        await tracked.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return receipt;
    }

    /// <summary>
    /// Reconnects to a known Codex thread without starting duplicate work: no
    /// new prompt is sent, the same thread identity is tracked, and the
    /// receipt preserves the exact correlation. A fresh observer process is
    /// spawned only when this adapter is not already tracking the thread
    /// (for example after a host restart); concurrent resumes for one thread
    /// launch at most one process.
    /// </summary>
    public async ValueTask<ExternalOperationResumeReceipt> ResumeAsync(
        ExternalOperationResumeRequest request,
        IExternalOperationHandleCaptureSink handleSink,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(handleSink);
        cancellationToken.ThrowIfCancellationRequested();
        var threadId = request.Handle.Value;
        TrackedOperation tracked;
        lock (launchGate)
        {
            if (!operations.TryGetValue(threadId, out var existing))
            {
                // Spawning here is synchronous and short; the slot is published
                // before the launch with no awaits inside.
                EnsureWorkspaceForLaunch();
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

                existing = new TrackedOperation(process, options.MaxOutputBytes)
                {
                    ThreadId = threadId,
                };
                existing.BindHandle(request.Handle);
                existing.ThreadFound.TrySetResult(threadId);
                existing.Sequence = System.Threading.Interlocked.Increment(ref startSequence);
                operations[threadId] = existing;
                existing.Pump = PumpAsync(existing);
                EvictCompleted();
            }

            tracked = existing;
        }

        // Correlation is fenced even on the already-tracked path: the same
        // thread value with different delegation/generation/attempt (or a
        // rotated provider/protocol) is rejected, never served.
        if (tracked.ExpectedHandle is null || tracked.ExpectedHandle != request.Handle)
        {
            throw new InvalidOperationException(
                $"Handle for Codex thread '{threadId}' does not match the tracked delegation/attempt correlation.");
        }

        await handleSink.CaptureAsync(
            new ExternalOperationHandleCapture(request.Handle, now()), cancellationToken).ConfigureAwait(false);
        var state = tracked.Completed ? tracked.ProjectedState : ExternalOperationState.Running;
        return new ExternalOperationResumeReceipt(
            request.Handle, request.ResumeKey, request.Handle,
            ExternalOperationStartDisposition.Existing, state, now());
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

    private async Task<string> WaitForThreadAsync(
        TrackedOperation tracked,
        ICodexProcess process,
        DateTimeOffset? deadline,
        CancellationToken cancellationToken)
    {
        // Bound the wait by the request deadline when the host supplied one:
        // a child that starts but never reports a thread identity (and never
        // exits) must not park the start forever.
        var threadOrCompletion = Task.WhenAny(tracked.ThreadFound.Task, tracked.Completion.Task);
        if (deadline is { } limit)
        {
            var remaining = limit - now();
            if (remaining <= TimeSpan.Zero)
            {
                KillQuietly(process);
                throw StartTimeout();
            }

            if (remaining <= MaximumTimerDelay)
            {
                using var timer = new CancellationTokenSource();
                var delay = Task.Delay(remaining, timer.Token);
                var raced = await Task.WhenAny(threadOrCompletion, delay).ConfigureAwait(false);
                if (raced == delay)
                {
                    KillQuietly(process);
                    throw StartTimeout();
                }

                timer.Cancel();
            }
        }

        var winner = await threadOrCompletion.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (winner == tracked.ThreadFound.Task && !string.IsNullOrWhiteSpace(tracked.ThreadId))
        {
            return tracked.ThreadId;
        }

        KillQuietly(process);
        throw new ExternalOperationProviderException(new ExternalOperationFailure(
            ExternalOperationFailureKind.Transport, "codex.no-thread",
            tracked.Failure is null
                ? tracked.CancelRequested
                    ? "Codex operation was cancelled before reporting a thread identity."
                    : "Codex ended before reporting a thread identity."
                : $"Codex ended before reporting a thread identity: {tracked.Failure.Summary}",
            retryable: false));
    }

    private static readonly TimeSpan MaximumTimerDelay = TimeSpan.FromMilliseconds(int.MaxValue - 1);

    private static void KillQuietly(ICodexProcess process)
    {
        try
        {
            process.Kill();
        }
        catch (InvalidOperationException)
        {
            // Already exited while failing.
        }
    }

    private static ExternalOperationProviderException StartTimeout() =>
        new(new ExternalOperationFailure(
            ExternalOperationFailureKind.Transport, "codex.start-timeout",
            "The provider deadline elapsed before Codex reported a thread identity.",
            retryable: false));

    private async Task PumpAsync(TrackedOperation tracked)
    {
        // Both pipes are drained concurrently: a child that fills stderr
        // while the adapter waits for stdout (or exit) would otherwise block
        // forever. Stderr is diagnostics only; stdout alone drives events.
        Task? stdoutDrain = null;
        Task? stderrDrain = null;
        try
        {
            // A faulted drain kills the child so its sibling drain (which may
            // be waiting on more output) terminates instead of wedging the
            // pump: without this, one failed stream plus one pending stream
            // would wait for each other forever with no live owner to kill.
            stdoutDrain = WatchDrainAsync(DrainStdoutAsync(tracked), tracked);
            stderrDrain = WatchDrainAsync(DrainStderrAsync(tracked), tracked);
            await Task.WhenAll(stdoutDrain, stderrDrain).ConfigureAwait(false);

            var exit = await tracked.Process.WaitForExitAsync().ConfigureAwait(false);
            tracked.ExitCode = exit;
            if (exit != 0 && tracked.Failure is null && !tracked.CancelRequested)
            {
                tracked.Fail(new ExternalOperationFailure(
                    ExternalOperationFailureKind.Remote, "codex.exit-failed",
                    $"Codex exited with code {exit} and no structured error.", retryable: false));
            }

            if (exit == 0 && tracked.Failure is null && !tracked.CancelRequested && !tracked.TurnCompletedSeen)
            {
                // A clean exit without a completed turn proves nothing: the
                // run may have printed chatter, been cut off, or never
                // reached its task. Verified completion requires turn
                // evidence, so this fails closed instead of claiming success.
                tracked.Fail(new ExternalOperationFailure(
                    ExternalOperationFailureKind.Remote, "codex.incomplete-evidence",
                    "Codex exited without a completed turn; success cannot be verified.",
                    retryable: false));
            }

            // The pump alone records the terminal outcome, exactly once, from
            // intent plus confirmed evidence.
            tracked.TrySetTerminal();
        }
        catch (Exception exception)
        {
            // Any drain failure (including cancellation of the pump itself)
            // fails the operation closed instead of faulting this
            // fire-and-forget task or leaving a live untracked child.
            tracked.Fail(new ExternalOperationFailure(
                ExternalOperationFailureKind.Transport, "codex.pump-failed",
                $"Codex output pump failed: {exception.GetType().Name}.", retryable: false));
        }
        finally
        {
            // Coordinate the end of the I/O lifecycle: the child is dead
            // before completion is published and the handle is disposed.
            // Kill is idempotent across exit races.
            try
            {
                tracked.Process.Kill();
            }
            catch (InvalidOperationException)
            {
                // Already exited while failing.
            }

            if (stdoutDrain is not null)
            {
                await SettleDrainAsync(stdoutDrain).ConfigureAwait(false);
            }

            if (stderrDrain is not null)
            {
                await SettleDrainAsync(stderrDrain).ConfigureAwait(false);
            }

            tracked.ThreadFound.TrySetResult(tracked.ThreadId);
            tracked.TrySetTerminal();
            tracked.FreezeReceipt(now());
            tracked.Complete();
            await tracked.Process.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task DrainStdoutAsync(TrackedOperation tracked)
    {
        await foreach (var line in tracked.Process.ReadLinesAsync().ConfigureAwait(false))
        {
            tracked.NoteLine(line);
            switch (CodexJsonlEvent.TryParse(line))
            {
                case CodexJsonlEvent.ThreadStarted started
                    when !string.IsNullOrWhiteSpace(started.ThreadId):
                    tracked.EventsObserved++;
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
                case CodexJsonlEvent.TurnStarted:
                    tracked.EventsObserved++;
                    break;
                case CodexJsonlEvent.TurnCompleted completed:
                    tracked.NoteTurnCompleted(completed.Usage);
                    break;
                case CodexJsonlEvent.ErrorEvent error:
                    tracked.EventsObserved++;
                    tracked.Fail(ClassifyError(error.Message));
                    break;
                case CodexJsonlEvent.TurnFailed failed:
                    tracked.EventsObserved++;
                    tracked.Fail(ClassifyError(failed.Message));
                    break;
                case CodexJsonlEvent.Unknown:
                    tracked.UnknownEvents++;
                    break;
                default:
                    break;
            }
        }
    }

    private static async Task DrainStderrAsync(TrackedOperation tracked)
    {
        // Keep draining even past the retention budget so a noisy child can
        // never block on a full stderr pipe; only capture is bounded.
        await foreach (var line in tracked.Process.ReadErrorLinesAsync().ConfigureAwait(false))
        {
            tracked.NoteStderr(line);
        }
    }

    private static async Task SettleDrainAsync(Task drain)
    {
        // The operation outcome is already recorded (first failure wins);
        // here the pump only ensures no drain is left running past disposal.
        try
        {
            await drain.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Already captured as codex.pump-failed by the pump handler.
        }
    }

    private static async Task WatchDrainAsync(Task drain, TrackedOperation tracked)
    {
        try
        {
            await drain.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Unblock the sibling drain: killing releases a hanging stream
            // (EOF after death) so WhenAll can reach the pump handler, which
            // records the failure and finishes lifecycle coordination.
            try
            {
                tracked.Process.Kill();
            }
            catch (InvalidOperationException)
            {
                // Already exited while failing.
            }

            throw;
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

        // Lookup fences the full operation identity, not just the thread
        // string: provider, protocol and the exact delegation/generation/
        // attempt correlation must match what the tracked launch bound.
        if (tracked.ExpectedHandle is null || tracked.ExpectedHandle != handle)
        {
            throw new InvalidOperationException(
                $"Handle for Codex thread '{handle.Value}' does not match the tracked delegation/attempt correlation.");
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

        // Oldest first, acknowledged receipts before unread ones: a retrieved
        // result is safe to drop while unread results survive the retention
        // policy. Unread evictions past capacity are counted, never silent.
        List<string>? removed = null;
        while (operations.Count > capacity)
        {
            var victim = operations
                .Where(entry => entry.Value.Completed)
                .OrderBy(entry => entry.Value.ResultRetrieved ? 0 : 1)
                .ThenBy(entry => entry.Value.Sequence)
                .Select(entry => entry.Key)
                .FirstOrDefault();
            if (victim is null
                || !operations.TryGetValue(victim, out var tracked)
                || !operations.TryRemove(victim, out _))
            {
                break;
            }

            if (!tracked.ResultRetrieved)
            {
                System.Threading.Interlocked.Increment(ref evictedUnreadReceipts);
            }

            removed ??= [];
            removed.Add(victim);
        }

        if (removed is null)
        {
            return;
        }

        lock (launchGate)
        {
            foreach (var bindingKey in startBindings.Keys.ToArray())
            {
                if (startBindings.TryGetValue(bindingKey, out var binding)
                    && removed.Contains(binding.ThreadId))
                {
                    startBindings.Remove(bindingKey);
                }
            }
        }
    }

    /// <summary>Gets the number of unread receipts evicted past capacity (diagnostics and tests).</summary>
    internal long EvictedUnreadReceipts => evictedUnreadReceipts;

    /// <summary>
    /// Frozen terminal receipt for one tracked operation: completion time,
    /// state, normalized output, exit code and provenance are captured once
    /// when the operation terminalizes, so repeated reads are identical.
    /// </summary>
    /// <param name="CompletedAt">Frozen completion timestamp.</param>
    /// <param name="State">Projected terminal state.</param>
    /// <param name="Summary">Frozen bounded transcript tail.</param>
    /// <param name="ExitCode">Process exit code, or -1 when the pump failed before exit.</param>
    /// <param name="Usage">Last parsed turn usage, or null when unknown.</param>
    /// <param name="ThreadId">Captured Codex thread identity.</param>
    /// <param name="EventsObserved">Modeled stdout events consumed.</param>
    /// <param name="UnknownEvents">Unmodeled stdout events counted.</param>
    /// <param name="Truncated">Whether stdout capture hit its budget.</param>
    /// <param name="StderrTruncated">Whether stderr capture hit its budget.</param>
    internal sealed record CodexTerminalReceipt(
        DateTimeOffset CompletedAt,
        ExternalOperationState State,
        string Summary,
        int ExitCode,
        CodexJsonlEvent.CodexUsage? Usage,
        string ThreadId,
        int EventsObserved,
        int UnknownEvents,
        bool Truncated,
        bool StderrTruncated);

    /// <summary>Terminal outcome of one tracked operation, recorded exactly once.</summary>
    internal enum TrackedOutcome
    {
        /// <summary>Not terminal.</summary>
        None = 0,

        /// <summary>Exited cleanly with no failure and no cancellation intent.</summary>
        Succeeded = 1,

        /// <summary>A failure won the terminal transition.</summary>
        Failed = 2,

        /// <summary>Cancellation intent was recorded and no failure won.</summary>
        Cancelled = 3,
    }

    private sealed class TrackedOperation
    {
        private readonly long maxBytes;
        private long bytes;
        private long stderrBytes;
        private ExternalOperationFailure? failure;

        // One gate for the whole lifecycle: intent, terminal transition,
        // cancellation keys and the bound handle. Drains record failures
        // lock-free (first wins); the terminal outcome is decided once,
        // under this gate, from intent plus recorded evidence.
        private readonly object gate = new();
        private bool cancelling;
        private TrackedOutcome outcome = TrackedOutcome.None;
        private string? cancelKey;
        private ExternalOperationCancellationReceipt? cancelReceipt;

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

        /// <summary>Gets the full handle this thread was bound to at launch or resume.</summary>
        public ExternalOperationHandle? ExpectedHandle { get; private set; }

        /// <summary>Binds the launched or resumed handle once, before tracking publishes.</summary>
        public void BindHandle(ExternalOperationHandle handle)
        {
            ArgumentNullException.ThrowIfNull(handle);
            lock (gate)
            {
                ExpectedHandle ??= handle;
            }
        }

        public List<string> Transcript { get; } = [];

        public bool Truncated { get; private set; }

        public long TranscriptBytes => bytes;

        public List<string> StderrTranscript { get; } = [];

        public bool StderrTruncated { get; private set; }

        public long StderrTranscriptBytes => stderrBytes;

        public int UnknownEvents { get; set; }

        public int EventsObserved { get; set; }

        /// <summary>Gets whether a completed turn was observed (verified-completion evidence).</summary>
        public bool TurnCompletedSeen { get; private set; }

        /// <summary>Gets the last parsed turn usage, or null when unknown.</summary>
        public CodexJsonlEvent.CodexUsage? Usage { get; private set; }

        /// <summary>Gets the process exit code, or -1 when the pump failed before exit.</summary>
        public int ExitCode { get; set; } = -1;

        /// <summary>Gets the frozen terminal receipt, once terminalized.</summary>
        public CodexTerminalReceipt? TerminalReceipt { get; private set; }

        /// <summary>Gets whether a terminal read acknowledged this operation (retention).</summary>
        public bool ResultRetrieved { get; set; }

        /// <summary>Gets the registration order used for oldest-first eviction.</summary>
        public long Sequence { get; set; }

        /// <summary>Records a completed turn: completion evidence plus last-wins usage.</summary>
        public void NoteTurnCompleted(CodexJsonlEvent.CodexUsage? usage)
        {
            TurnCompletedSeen = true;
            EventsObserved++;
            if (usage is not null)
            {
                Usage = usage;
            }
        }

        /// <summary>Freezes time, state, output and provenance once, before completion publishes.</summary>
        public void FreezeReceipt(DateTimeOffset completedAt)
        {
            TerminalReceipt ??= new CodexTerminalReceipt(
                completedAt,
                ProjectedState,
                Summary(),
                ExitCode,
                Usage,
                ThreadId,
                EventsObserved,
                UnknownEvents,
                Truncated,
                StderrTruncated);
        }

        public long ObservationsServed;

        public ExternalOperationFailure? Failure => Volatile.Read(ref failure);

        /// <summary>Whether cancellation intent was ever recorded.</summary>
        public bool CancelRequested
        {
            get
            {
                lock (gate)
                {
                    return cancelling;
                }
            }
        }

        /// <summary>Whether exactly one terminal outcome was recorded.</summary>
        public bool Completed
        {
            get
            {
                lock (gate)
                {
                    return outcome != TrackedOutcome.None;
                }
            }
        }

        /// <summary>Gets the recorded terminal outcome (<see cref="TrackedOutcome.None"/> while running).</summary>
        public TrackedOutcome Outcome
        {
            get
            {
                lock (gate)
                {
                    return outcome;
                }
            }
        }

        /// <summary>
        /// Projects the terminal outcome to a contract-valid state. A
        /// Transport-kind failure cannot be reported as
        /// <see cref="ExternalOperationState.Failed"/> (the frozen contracts
        /// only accept Remote/ResultValidation there), so it surfaces as
        /// <see cref="ExternalOperationState.Unknown"/>: the adapter cannot
        /// confirm what the provider did.
        /// </summary>
        public ExternalOperationState ProjectedState
        {
            get
            {
                lock (gate)
                {
                    return ProjectedStateLocked();
                }
            }
        }

        private ExternalOperationState ProjectedStateLocked() => outcome switch
        {
            TrackedOutcome.Succeeded => ExternalOperationState.Succeeded,
            TrackedOutcome.Cancelled => ExternalOperationState.Cancelled,
            TrackedOutcome.Failed => failure is not null
                && failure.Kind is ExternalOperationFailureKind.Remote
                    or ExternalOperationFailureKind.ResultValidation
                ? ExternalOperationState.Failed
                : ExternalOperationState.Unknown,
            _ => throw new InvalidOperationException("The operation has no terminal outcome yet."),
        };

        // First failure wins across the concurrent stdout/stderr drains.
        public void Fail(ExternalOperationFailure operationFailure) =>
            Interlocked.CompareExchange(ref failure, operationFailure, null);

        /// <summary>
        /// Records the single terminal outcome: cancellation intent wins over
        /// a clean exit, confirmed failure wins over late cancellation, and a
        /// clean run succeeds. Returns false when already terminal.
        /// </summary>
        public bool TrySetTerminal()
        {
            lock (gate)
            {
                if (outcome != TrackedOutcome.None)
                {
                    return false;
                }

                outcome = cancelling && failure is null
                    ? TrackedOutcome.Cancelled
                    : failure is not null
                        ? TrackedOutcome.Failed
                        : TrackedOutcome.Succeeded;
                return true;
            }
        }

        public void Complete()
        {
            Completion.TrySetResult();
        }

        /// <summary>
        /// Records cancellation intent (killing once) or replays the stored
        /// receipt. A terminal operation keeps its outcome and reports
        /// <see cref="ExternalOperationCancellationDisposition.AlreadyTerminal"/>;
        /// a different key on live intent conflicts.
        /// </summary>
        public ExternalOperationCancellationReceipt RequestCancel(
            string key,
            ExternalOperationHandle handle,
            DateTimeOffset timestamp)
        {
            ExternalOperationCancellationReceipt receipt;
            var kill = false;
            lock (gate)
            {
                if (outcome != TrackedOutcome.None)
                {
                    if (cancelReceipt is not null && string.Equals(key, cancelKey, StringComparison.Ordinal))
                    {
                        return cancelReceipt;
                    }

                    return new ExternalOperationCancellationReceipt(
                        handle, key,
                        ExternalOperationCancellationDisposition.AlreadyTerminal,
                        ProjectedStateLocked(), timestamp);
                }

                if (cancelKey is not null)
                {
                    if (string.Equals(key, cancelKey, StringComparison.Ordinal))
                    {
                        return cancelReceipt!;
                    }

                    throw new ExternalOperationProviderException(new ExternalOperationFailure(
                        ExternalOperationFailureKind.Rejection, "codex.cancel-conflict",
                        "A different cancellation key is already recorded for this operation.",
                        retryable: false));
                }

                cancelKey = key;
                cancelling = true;
                receipt = new ExternalOperationCancellationReceipt(
                    handle, key,
                    ExternalOperationCancellationDisposition.Requested,
                    ExternalOperationState.CancellationRequested, timestamp);
                cancelReceipt = receipt;
                kill = true;
            }

            if (kill)
            {
                try
                {
                    Process.Kill();
                }
                catch (InvalidOperationException)
                {
                    // Already exited between the intent and the kill.
                }
            }

            return receipt;
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

        public void NoteStderr(string line)
        {
            // Stderr is classified separately from the stdout transcript but
            // shares the configured byte-budget value as its own account:
            // retention is bounded while the drain itself never stops early.
            var size = System.Text.Encoding.UTF8.GetByteCount(line) + 1;
            if (stderrBytes + size > maxBytes)
            {
                StderrTruncated = true;
                return;
            }

            stderrBytes += size;
            StderrTranscript.Add(line);
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
