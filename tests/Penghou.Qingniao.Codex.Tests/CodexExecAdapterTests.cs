using FluentAssertions;

namespace Penghou.Qingniao.Codex.Tests;

/// <summary>
/// Adapter tests against recorded and scripted process output. The usage-limit
/// probe is a verbatim recorded `codex exec --json` transcript; success
/// content stays out of scope until a live run records its schema.
/// </summary>
public sealed class CodexExecAdapterTests
{
    private static readonly DelegationId Delegation = new(Guid.Parse("00000000-0000-0000-0000-000000000001"));
    private static readonly WorkflowRunExecutionReference Workflow = new("test-host", "run-1", "epoch-1");
    private static readonly StructuralNodeReference Node = new("implement");
    private static readonly NodeGenerationId Generation = new(Guid.Parse("00000000-0000-0000-0000-000000000011"));
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static readonly ExternalAgentReference Agent = new("codex", "codex-agent", "codex.exec.v1");

    private const string RecordedThreadId = "01a0d765-251e-71c2-9ed5-fb006e9c7623";
    private static readonly string[] RecordedUsageLimitProbe =
    [
        """{"type":"thread.started","thread_id":"01a0d765-251e-71c2-9ed5-fb006e9c7623"}""",
        """{"type":"turn.started"}""",
        """{"type":"error","message":"You hit your usage limit."}""",
        """{"type":"turn.failed","error":{"message":"You hit your usage limit."}}""",
    ];

    // Reconstructed from the 2026-09-26 live proof on codex-cli 0.157.0, which
    // observed thread.started first, turn.started/turn.completed framing, and
    // input/cached/output token shapes. The full CLI schema is unverified;
    // see docs/codex-protocol-support.md.
    private static string[] Cli0157SuccessWithUsage(string threadId) =>
    [
        """{"type":"thread.started","thread_id":"THREAD"}""".Replace("THREAD", threadId, StringComparison.Ordinal),
        """{"type":"turn.started"}""",
        """{"type":"turn.completed","usage":{"input_tokens":120,"cached_input_tokens":30,"output_tokens":45}}""",
    ];

    [Fact]
    public async Task StartAsync_captures_thread_then_reports_recorded_usage_failure()
    {
        var factory = new ScriptedProcessFactory((_, _) =>
            new ScriptedProcess(RecordedUsageLimitProbe, exitCode: 1));
        var adapter = CreateAdapter(factory);
        var sink = new RecordingHandleSink();
        var ct = TestContext.Current.CancellationToken;

        var receipt = await adapter.StartAsync(StartRequest(), sink, ct);

        sink.Captures.Should().ContainSingle();
        sink.Captures[0].Handle.Value.Should().Be(RecordedThreadId);
        receipt.Handle.Value.Should().Be(RecordedThreadId);
        receipt.State.Should().Be(ExternalOperationState.Failed);
        factory.Spawns.Should().Be(1);

        var status = await adapter.ObserveAsync(receipt.Handle, ct);
        status.State.Should().Be(ExternalOperationState.Failed);

        var act = () => adapter.GetResultAsync(receipt.Handle, ct).AsTask();
        var failure = (await act.Should().ThrowAsync<ExternalOperationProviderException>()).Which.Failure;
        failure.Code.Should().Be("codex.usage-limit");
        failure.Kind.Should().Be(ExternalOperationFailureKind.Remote);
        failure.Retryable.Should().BeFalse();
    }

    [Fact]
    public async Task StartAsync_completes_verified_lifecycle_with_turn_evidence()
    {
        var factory = new ScriptedProcessFactory((_, _) => new ScriptedProcess(
            Cli0157SuccessWithUsage("thread-1"),
            exitCode: 0));
        var adapter = CreateAdapter(factory);
        var sink = new RecordingHandleSink();
        var ct = TestContext.Current.CancellationToken;

        var receipt = await adapter.StartAsync(StartRequest(), sink, ct);
        receipt.Handle.Value.Should().Be("thread-1");
        // The scripted process may already have finished; the start receipt
        // then reports the real terminal state instead of a stale Running.
        receipt.State.Should().BeOneOf(
            ExternalOperationState.Running,
            ExternalOperationState.Succeeded);

        var result = await adapter.GetResultAsync(receipt.Handle, ct);
        result.State.Should().Be(ExternalOperationState.Succeeded);
        result.Handle.Should().Be(receipt.Handle);
    }

    [Fact]
    public async Task StartAsync_exit_without_completed_turn_cannot_claim_success()
    {
        var factory = new ScriptedProcessFactory((_, _) => new ScriptedProcess(
            [
                """{"type":"thread.started","thread_id":"thread-bare"}""",
                """{"type":"turn.started"}""",
            ],
            exitCode: 0));
        var adapter = CreateAdapter(factory);
        var sink = new RecordingHandleSink();
        var ct = TestContext.Current.CancellationToken;

        // The thread capture receipt predates the terminal outcome; only the
        // terminal reads carry the verified result.
        var receipt = await adapter.StartAsync(StartRequest(), sink, ct);
        receipt.Handle.Value.Should().Be("thread-bare");

        var act = () => adapter.GetResultAsync(receipt.Handle, ct).AsTask();
        var failure = (await act.Should().ThrowAsync<ExternalOperationProviderException>()).Which.Failure;
        failure.Code.Should().Be("codex.incomplete-evidence");
        failure.Kind.Should().Be(ExternalOperationFailureKind.Remote);

        var status = await adapter.ObserveAsync(receipt.Handle, ct);
        status.State.Should().Be(ExternalOperationState.Failed);
        status.Failure!.Code.Should().Be("codex.incomplete-evidence");
    }

    [Fact]
    public async Task Repeated_terminal_reads_are_identical()
    {
        var factory = new ScriptedProcessFactory((_, _) => new ScriptedProcess(
            Cli0157SuccessWithUsage("thread-repeat"),
            exitCode: 0));
        var adapter = CreateAdapter(factory);
        var sink = new RecordingHandleSink();
        var ct = TestContext.Current.CancellationToken;

        var receipt = await adapter.StartAsync(StartRequest(), sink, ct);
        var first = await adapter.GetResultAsync(receipt.Handle, ct);
        var second = await adapter.GetResultAsync(receipt.Handle, ct);

        second.Summary.Should().Be(first.Summary);
        second.CompletedAt.Should().Be(first.CompletedAt);
        second.State.Should().Be(ExternalOperationState.Succeeded);

        var frozen = adapter.GetTerminalReceipt(receipt.Handle);
        frozen.Should().NotBeNull();
        frozen!.CompletedAt.Should().Be(first.CompletedAt);
        frozen.ExitCode.Should().Be(0);
        frozen.Usage.Should().Be(new CodexJsonlEvent.CodexUsage(120, 30, 45));
        frozen.ThreadId.Should().Be("thread-repeat");
        frozen.EventsObserved.Should().Be(3);
        frozen.UnknownEvents.Should().Be(0);
        frozen.Truncated.Should().BeFalse();

        var firstStatus = await adapter.ObserveAsync(receipt.Handle, ct);
        var secondStatus = await adapter.ObserveAsync(receipt.Handle, ct);
        firstStatus.State.Should().Be(ExternalOperationState.Succeeded);
        secondStatus.State.Should().Be(ExternalOperationState.Succeeded);
    }

    [Fact]
    public async Task Unread_receipts_survive_retention_policy()
    {
        var counter = 0;
        var factory = new ScriptedProcessFactory((_, _) =>
        {
            var id = Interlocked.Increment(ref counter);
            return new ScriptedProcess(Cli0157SuccessWithUsage("thread-" + id), exitCode: 0);
        });
        var adapter = CreateAdapter(factory);
        var sink = new RecordingHandleSink();
        var ct = TestContext.Current.CancellationToken;

        // Fill to capacity without reading: every receipt survives.
        var handles = new List<ExternalOperationHandle>();
        for (var index = 0; index < 128; index++)
        {
            var receipt = await adapter.StartAsync(StartRequest(startKey: $"retention-{index}"), sink, ct);
            await WaitForTerminalAsync(adapter, receipt.Handle, ct);
            handles.Add(receipt.Handle);
        }

        adapter.TrackedOperationCount.Should().Be(128);
        adapter.EvictedUnreadReceipts.Should().Be(0);

        // One past capacity evicts the oldest unread receipt, counted aloud.
        var overflow = await adapter.StartAsync(StartRequest(startKey: "retention-overflow"), sink, ct);
        await WaitForTerminalAsync(adapter, overflow.Handle, ct);
        adapter.TrackedOperationCount.Should().Be(128);
        adapter.EvictedUnreadReceipts.Should().Be(1);
        var evicted = () => adapter.ObserveAsync(handles[0], ct).AsTask();
        await evicted.Should().ThrowAsync<InvalidOperationException>();
        var survivor = await adapter.ObserveAsync(handles[1], ct);
        survivor.State.Should().Be(ExternalOperationState.Succeeded);

        // Acknowledged receipts evict before unread ones: read ten, add ten,
        // and the reads (not the unread survivors) make room.
        for (var index = 1; index <= 10; index++)
        {
            _ = await adapter.GetResultAsync(handles[index], ct);
        }

        for (var index = 0; index < 10; index++)
        {
            var receipt = await adapter.StartAsync(StartRequest(startKey: $"retention-second-{index}"), sink, ct);
            await WaitForTerminalAsync(adapter, receipt.Handle, ct);
        }

        adapter.TrackedOperationCount.Should().Be(128);
        adapter.EvictedUnreadReceipts.Should().Be(1);
        for (var index = 1; index <= 10; index++)
        {
            var gone = () => adapter.ObserveAsync(handles[index], ct).AsTask();
            await gone.Should().ThrowAsync<InvalidOperationException>();
        }

        var kept = await adapter.ObserveAsync(handles[11], ct);
        kept.State.Should().Be(ExternalOperationState.Succeeded);
    }

    [Fact]
    public async Task Cold_resume_preserves_work_identity()
    {
        var factory = new ScriptedProcessFactory((invocation, _) =>
        {
            invocation.Arguments.Should().Contain("resume");
            invocation.Arguments.Should().Contain("thread-cold");
            return new ScriptedProcess(
                Cli0157SuccessWithUsage("thread-cold"),
                exitCode: 0);
        });
        var adapter = CreateAdapter(factory);
        var sink = new RecordingHandleSink();
        var ct = TestContext.Current.CancellationToken;

        // The adapter never tracked this thread: the observer reconnects it
        // without starting fresh semantic work.
        var handle = TrackedHandle("thread-cold");
        var resume = await adapter.ResumeAsync(
            new ExternalOperationResumeRequest(handle, "resume-cold"), sink, ct);

        resume.Disposition.Should().Be(ExternalOperationStartDisposition.Existing);
        resume.Handle.Should().Be(handle);
        factory.Spawns.Should().Be(1);

        var result = await adapter.GetResultAsync(handle, ct);
        result.State.Should().Be(ExternalOperationState.Succeeded);
        var frozen = adapter.GetTerminalReceipt(handle);
        frozen!.ThreadId.Should().Be("thread-cold");
        frozen.Usage.Should().Be(new CodexJsonlEvent.CodexUsage(120, 30, 45));
    }

    [Fact]
    public async Task Missing_session_resume_fails_explicitly()
    {
        var factory = new ScriptedProcessFactory((_, _) => new ScriptedProcess(
            ["""{"type":"error","message":"synthetic session lookup failure"}"""],
            exitCode: 1));
        var adapter = CreateAdapter(factory);
        var sink = new RecordingHandleSink();
        var ct = TestContext.Current.CancellationToken;

        // Synthetic stand-in: no verified CLI missing-session shape exists
        // yet (see docs/codex-protocol-support.md). Whatever the CLI reports,
        // an errored observer must fail closed, never recover silently.
        var handle = TrackedHandle("thread-gone");
        var resume = await adapter.ResumeAsync(
            new ExternalOperationResumeRequest(handle, "resume-gone"), sink, ct);

        resume.Handle.Value.Should().Be("thread-gone");
        var act = () => adapter.GetResultAsync(handle, ct).AsTask();
        var failure = (await act.Should().ThrowAsync<ExternalOperationProviderException>()).Which.Failure;
        failure.Code.Should().Be("codex.remote-error");

        var status = await adapter.ObserveAsync(handle, ct);
        status.State.Should().Be(ExternalOperationState.Failed);
    }

    [Fact]
    public async Task Resume_response_loss_reconnects_same_attempt()
    {
        var factory = new ScriptedProcessFactory((_, _) => new ScriptedProcess(
            ["""{"type":"thread.started","thread_id":"thread-loss"}"""],
            exitCode: 0,
            hangAfterLines: true));
        var adapter = CreateAdapter(factory);
        var startSink = new RecordingHandleSink();
        var resumeSink = new FlakyHandleSink(calls => calls == 1);
        var ct = TestContext.Current.CancellationToken;

        var receipt = await adapter.StartAsync(StartRequest(), startSink, ct);
        var lost = () => adapter.ResumeAsync(
            new ExternalOperationResumeRequest(receipt.Handle, "resume-loss"), resumeSink, ct).AsTask();
        await lost.Should().ThrowAsync<InvalidOperationException>();

        var resume = await adapter.ResumeAsync(
            new ExternalOperationResumeRequest(receipt.Handle, "resume-loss"), resumeSink, ct);

        resume.Disposition.Should().Be(ExternalOperationStartDisposition.Existing);
        resume.Handle.Should().Be(receipt.Handle);
        factory.Spawns.Should().Be(1);
        resumeSink.Captures.Should().ContainSingle();
    }

    [Fact]
    public async Task StartAsync_deadline_elapsed_before_thread_is_refused()
    {
        var factory = new ScriptedProcessFactory((_, _) => new ScriptedProcess(
            [],
            exitCode: 0,
            hangAfterLines: true));
        var adapter = CreateAdapter(factory);
        var ct = TestContext.Current.CancellationToken;

        // A deadline already in the past bounds a start that never reports a
        // thread identity and never exits.
        var act = () => adapter.StartAsync(
            StartRequest(deadline: DateTimeOffset.UtcNow.AddMinutes(-1)), new RecordingHandleSink(), ct).AsTask();

        var failure = (await act.Should().ThrowAsync<ExternalOperationProviderException>()).Which.Failure;
        failure.Code.Should().Be("codex.start-timeout");
        failure.Kind.Should().Be(ExternalOperationFailureKind.Transport);
        failure.Retryable.Should().BeFalse();
        factory.Spawns.Should().Be(1);
        factory.Processes.Should().ContainSingle().Which.Killed.Should().BeTrue();
    }

    [Fact]
    public async Task StartAsync_future_deadline_captures_thread_normally()
    {
        var factory = new ScriptedProcessFactory((_, _) => new ScriptedProcess(
            Cli0157SuccessWithUsage("thread-deadline"),
            exitCode: 0));
        var adapter = CreateAdapter(factory);
        var sink = new RecordingHandleSink();
        var ct = TestContext.Current.CancellationToken;

        var receipt = await adapter.StartAsync(
            StartRequest(deadline: DateTimeOffset.UtcNow.AddDays(1)), sink, ct);

        receipt.Handle.Value.Should().Be("thread-deadline");
        (await adapter.GetResultAsync(receipt.Handle, ct)).State.Should().Be(ExternalOperationState.Succeeded);
    }

    private static async Task WaitForTerminalAsync(
        CodexExecAdapter adapter,
        ExternalOperationHandle handle,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        while (true)
        {
            var status = await adapter.ObserveAsync(handle, linked.Token);
            if (status.State is ExternalOperationState.Succeeded
                or ExternalOperationState.Failed
                or ExternalOperationState.Cancelled)
            {
                return;
            }

            await Task.Delay(20, linked.Token);
        }
    }

    [Fact]
    public async Task StartAsync_without_thread_identity_reports_transport_failure()
    {
        var factory = new ScriptedProcessFactory((_, _) => new ScriptedProcess(
            ["""{"type":"error","message":"boom"}"""],
            exitCode: 1));
        var adapter = CreateAdapter(factory);
        var sink = new RecordingHandleSink();
        var ct = TestContext.Current.CancellationToken;

        var act = () => adapter.StartAsync(StartRequest(), sink, ct).AsTask();
        var failure = (await act.Should().ThrowAsync<ExternalOperationProviderException>()).Which.Failure;
        failure.Code.Should().Be("codex.no-thread");
        failure.Kind.Should().Be(ExternalOperationFailureKind.Transport);
        sink.Captures.Should().BeEmpty();
    }

    [Fact]
    public async Task ResumeAsync_reuses_tracked_thread_without_respawn()
    {
        var factory = new ScriptedProcessFactory((_, _) => new ScriptedProcess(
            ["""{"type":"thread.started","thread_id":"thread-9"}"""],
            exitCode: 0,
            hangAfterLines: true));
        var adapter = CreateAdapter(factory);
        var sink = new RecordingHandleSink();
        var ct = TestContext.Current.CancellationToken;

        var receipt = await adapter.StartAsync(StartRequest(), sink, ct);
        var resume = await adapter.ResumeAsync(
            new ExternalOperationResumeRequest(receipt.Handle, "resume-1"), sink, ct);

        factory.Spawns.Should().Be(1);
        resume.Disposition.Should().Be(ExternalOperationStartDisposition.Existing);
        resume.Handle.Should().Be(receipt.Handle);
        sink.Captures.Should().HaveCount(2);
    }

    [Fact]
    public async Task ResumeAsync_unknown_thread_spawns_resume_without_prompt_or_bypass()
    {
        var factory = new ScriptedProcessFactory((invocation, _) =>
        {
            invocation.Arguments.Should().Contain("resume");
            invocation.Arguments.Should().Contain("thread-x");
            invocation.Arguments.Should().NotContain("do the work");
            invocation.Arguments.Should().NotContain(arg => arg.StartsWith("--dangerously", StringComparison.Ordinal));
            return new ScriptedProcess(
                ["""{"type":"thread.started","thread_id":"thread-x"}"""],
                exitCode: 0,
                hangAfterLines: true);
        });
        var adapter = CreateAdapter(factory, prompt: "do the work");
        var sink = new RecordingHandleSink();
        var ct = TestContext.Current.CancellationToken;

        var handle = TrackedHandle("thread-x");
        var resume = await adapter.ResumeAsync(
            new ExternalOperationResumeRequest(handle, "resume-1"), sink, ct);

        resume.Handle.Value.Should().Be("thread-x");
        factory.Spawns.Should().Be(1);
    }

    [Fact]
    public async Task CancelAsync_kills_process_and_reports_cancellation()
    {
        var factory = new ScriptedProcessFactory((_, _) => new ScriptedProcess(
            ["""{"type":"thread.started","thread_id":"thread-7"}"""],
            exitCode: 0,
            hangAfterLines: true));
        var adapter = CreateAdapter(factory);
        var sink = new RecordingHandleSink();
        var ct = TestContext.Current.CancellationToken;

        var receipt = await adapter.StartAsync(StartRequest(), sink, ct);
        var cancel = await adapter.CancelAsync(
            new ExternalOperationCancelRequest(receipt.Handle, "cancel-1", "stop"), ct);

        cancel.State.Should().Be(ExternalOperationState.CancellationRequested);
        factory.Processes.Should().ContainSingle().Which.Killed.Should().BeTrue();

        var status = await adapter.ObserveAsync(receipt.Handle, ct);
        status.State.Should().Be(ExternalOperationState.Cancelled);

        var act = () => adapter.GetResultAsync(receipt.Handle, ct).AsTask();
        (await act.Should().ThrowAsync<ExternalOperationProviderException>())
            .Which.Failure.Code.Should().Be("codex.cancelled");
    }

    [Fact]
    public async Task StartAsync_spawn_failure_is_classified_transport()
    {
        var factory = new ScriptedProcessFactory((_, _) => throw new InvalidOperationException("no cli"));
        var adapter = CreateAdapter(factory);
        var sink = new RecordingHandleSink();
        var ct = TestContext.Current.CancellationToken;

        var act = () => adapter.StartAsync(StartRequest(), sink, ct).AsTask();
        var failure = (await act.Should().ThrowAsync<ExternalOperationProviderException>()).Which.Failure;
        failure.Code.Should().Be("codex.spawn-failed");
        failure.Kind.Should().Be(ExternalOperationFailureKind.Transport);
        sink.Captures.Should().BeEmpty();
    }

    [Fact]
    public async Task Concurrent_attached_start_is_released_when_spawn_is_cancelled()
    {
        var ct = TestContext.Current.CancellationToken;
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new ScriptedProcessFactory((_, _) =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5), ct))
            {
                throw new TimeoutException("Test spawn gate timed out.");
            }

            throw new OperationCanceledException("Spawn was cancelled.");
        });
        var adapter = CreateAdapter(factory);
        var sink = new RecordingHandleSink();
        var request = StartRequest();

        var owner = Task.Run(() => adapter.StartAsync(request, sink, ct).AsTask(), ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        var attached = adapter.StartAsync(request, sink, ct).AsTask();
        release.Set();

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await owner.WaitAsync(TimeSpan.FromSeconds(5), ct));
        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await attached.WaitAsync(TimeSpan.FromSeconds(5), ct));
        factory.Spawns.Should().Be(1);
        sink.Captures.Should().BeEmpty();
    }

    [Fact]
    public async Task Many_operations_evict_only_completed_tracking()
    {
        var counter = 0;
        var factory = new ScriptedProcessFactory((_, _) =>
        {
            var id = Interlocked.Increment(ref counter);
            return new ScriptedProcess(["{\"type\":\"thread.started\",\"thread_id\":\"thread-" + id + "\"}"], exitCode: 0);
        });
        var adapter = CreateAdapter(factory);
        var sink = new RecordingHandleSink();
        var ct = TestContext.Current.CancellationToken;

        for (var index = 0; index < 130; index++)
        {
            // Distinct keys: identical keys would attach to the tracked launch
            // instead of spawning, which is covered by the attach test.
            await adapter.StartAsync(StartRequest(startKey: $"start-key-{index}"), sink, ct);
        }

        adapter.TrackedOperationCount.Should().BeLessThanOrEqualTo(128);
        sink.Captures.Should().HaveCount(130);
    }

    [Fact]
    public async Task Resume_rejecting_changed_thread_identity()
    {
        var factory = new ScriptedProcessFactory((_, _) => new ScriptedProcess(
            ["""{"type":"thread.started","thread_id":"thread-other"}"""],
            exitCode: 0));
        var adapter = CreateAdapter(factory);
        var sink = new RecordingHandleSink();
        var ct = TestContext.Current.CancellationToken;

        var resume = await adapter.ResumeAsync(
            new ExternalOperationResumeRequest(TrackedHandle("thread-a"), "resume-1"), sink, ct);
        var act = () => adapter.GetResultAsync(resume.Handle, ct).AsTask();
        (await act.Should().ThrowAsync<ExternalOperationProviderException>())
            .Which.Failure.Code.Should().Be("codex.thread-changed");
    }

    [Fact]
    public async Task StartAsync_honors_caller_cancellation()
    {
        // No lines: the pump suspends before any thread identity, so the
        // cancelled token is observed while actually waiting.
        var factory = new ScriptedProcessFactory((_, _) => new ScriptedProcess(
            [],
            exitCode: 0,
            hangAfterLines: true));
        var adapter = CreateAdapter(factory);
        var sink = new RecordingHandleSink();

        var act = () => adapter.StartAsync(StartRequest(), sink, new CancellationToken(canceled: true)).AsTask();
        await act.Should().ThrowAsync<OperationCanceledException>();
        sink.Captures.Should().BeEmpty();
    }

    [Fact]
    public void Options_reject_workspace_outside_approved_root()
    {
        var factory = new ScriptedProcessFactory((_, _) => new ScriptedProcess([], exitCode: 0));
        var adapter = CreateAdapter(factory);
        _ = adapter;

        var act = () => new CodexExecOptions(
            "do the work",
            OperatingSystem.IsWindows() ? @"C:\elsewhere\work" : "/elsewhere/work",
            OperatingSystem.IsWindows() ? @"C:\approved" : "/approved");
        act.Should().Throw<ArgumentException>();
        factory.Spawns.Should().Be(0);
    }

    [Fact]
    public async Task Argv_selects_sandbox_and_never_bypasses()
    {
        var factory = new ScriptedProcessFactory((invocation, _) =>
        {
            invocation.Arguments.Should().Contain("--json");
            invocation.Arguments.Should().Contain("-s");
            invocation.Arguments.Should().Contain("read-only");
            invocation.Arguments.Should().NotContain(arg => arg.StartsWith("--dangerously", StringComparison.Ordinal));
            invocation.WorkingDirectory.Should().Be(Workspace());
            return new ScriptedProcess(
                ["""{"type":"thread.started","thread_id":"thread-s"}"""],
                exitCode: 0);
        });
        var root = WorkspaceRoot();
        var adapter = new CodexExecAdapter(
            new CodexExecOptions(
                "do the work",
                Path.Combine(root, "work"),
                root,
                cliPath: "codex",
                sandbox: CodexSandboxMode.ReadOnly),
            factory);
        var sink = new RecordingHandleSink();
        var ct = TestContext.Current.CancellationToken;

        var receipt = await adapter.StartAsync(StartRequest(), sink, ct);
        receipt.Handle.Value.Should().Be("thread-s");
    }

    [Fact]
    public async Task StartAsync_drains_stderr_into_separate_bounded_diagnostics()
    {
        var factory = new ScriptedProcessFactory((_, _) => new ScriptedProcess(
            [
                """{"type":"thread.started","thread_id":"thread-io"}""",
                """{"type":"turn.started"}""",
                """{"type":"turn.completed"}""",
            ],
            exitCode: 0,
            stderrLines:
            [
                "stderr-diag-line-1",
                "stderr-diag-line-2",
            ]));
        var adapter = CreateAdapter(factory);
        var sink = new RecordingHandleSink();
        var ct = TestContext.Current.CancellationToken;

        var receipt = await adapter.StartAsync(StartRequest(), sink, ct);
        var result = await adapter.GetResultAsync(receipt.Handle, ct);

        result.State.Should().Be(ExternalOperationState.Succeeded);
        // Stderr stays classified as diagnostics: it never leaks into the stdout result.
        result.Summary.Should().NotContain("stderr-diag");

        var io = adapter.GetIODiagnostics(receipt.Handle);
        io.StdoutLines.Should().Be(3);
        io.StdoutTruncated.Should().BeFalse();
        io.StderrLines.Should().Be(2);
        io.StderrBytes.Should().BePositive();
        io.StderrTruncated.Should().BeFalse();
        factory.Processes.Should().ContainSingle().Which.StderrDrained.Should().BeTrue();
    }

    [Fact]
    public async Task Stderr_flood_is_truncated_but_stdout_result_survives()
    {
        var flood = Enumerable.Range(0, 6_000).Select(index => new string('E', 199) + index).ToArray();
        var factory = new ScriptedProcessFactory((_, _) => new ScriptedProcess(
            [
                """{"type":"thread.started","thread_id":"thread-flood"}""",
                """{"type":"turn.started"}""",
                """{"type":"turn.completed"}""",
            ],
            exitCode: 0,
            stderrLines: flood));
        var adapter = CreateAdapter(factory);
        var sink = new RecordingHandleSink();
        var ct = TestContext.Current.CancellationToken;

        var receipt = await adapter.StartAsync(StartRequest(), sink, ct);
        var result = await adapter.GetResultAsync(receipt.Handle, ct);

        // Draining continues past the budget so the child finishes; only capture stops.
        result.State.Should().Be(ExternalOperationState.Succeeded);
        var io = adapter.GetIODiagnostics(receipt.Handle);
        io.StderrTruncated.Should().BeTrue();
        io.StderrBytes.Should().BeLessThanOrEqualTo(1_048_576);
        io.StdoutLines.Should().Be(3);
        io.StdoutTruncated.Should().BeFalse();
    }

    [Fact]
    public async Task Pump_failure_kills_disposes_and_reports_transport_without_live_child()
    {
        var factory = new ScriptedProcessFactory((_, _) => new ScriptedProcess(
            ["""{"type":"thread.started","thread_id":"thread-boom"}"""],
            exitCode: 0,
            hangAfterLines: true,
            stderrLines: ["late-stderr-kept-draining"],
            readFailure: new InvalidOperationException("boom")));
        var adapter = CreateAdapter(factory);
        var sink = new RecordingHandleSink();
        // Bound the wait: a pump that wedges on one failed stream must fail
        // this test, not hang the suite.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken, timeout.Token);
        var ct = linked.Token;

        // The resume path surfaces the pump outcome directly: the thread is
        // pre-registered, so no thread wait rewrites the failure.
        var resume = await adapter.ResumeAsync(
            new ExternalOperationResumeRequest(TrackedHandle("thread-boom"), "resume-1"), sink, ct);

        var act = () => adapter.GetResultAsync(resume.Handle, ct).AsTask();
        var failure = (await act.Should().ThrowAsync<ExternalOperationProviderException>()).Which.Failure;
        failure.Code.Should().Be("codex.pump-failed");
        failure.Kind.Should().Be(ExternalOperationFailureKind.Transport);

        // QH-03 projects Transport-kind terminals as Unknown: the frozen
        // contracts only accept Remote/ResultValidation for Failed states.
        var status = await adapter.ObserveAsync(resume.Handle, ct);
        status.State.Should().Be(ExternalOperationState.Unknown);
        status.Failure.Should().NotBeNull();
        status.Failure!.Code.Should().Be("codex.pump-failed");

        // The failed pump kills, settles both drains, completes and disposes.
        var process = factory.Processes.Should().ContainSingle().Which;
        await WaitUntilAsync(() => process.Disposed, ct);
        process.Killed.Should().BeTrue();
        process.StderrDrained.Should().BeTrue();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        while (!condition())
        {
            await Task.Delay(20, linked.Token);
        }
    }

    private const string Hash2 = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

    [Fact]
    public async Task StartAsync_precancelled_spawns_zero_processes()
    {
        var factory = new ScriptedProcessFactory((_, _) => new ScriptedProcess(
            ["""{"type":"thread.started","thread_id":"thread-never"}"""],
            exitCode: 0));
        var adapter = CreateAdapter(factory);

        var act = () => adapter.StartAsync(StartRequest(), new RecordingHandleSink(), new CancellationToken(canceled: true)).AsTask();

        await act.Should().ThrowAsync<OperationCanceledException>();
        factory.Spawns.Should().Be(0);
    }

    [Fact]
    public async Task Late_cancel_preserves_success()
    {
        var factory = new ScriptedProcessFactory((_, _) => new ScriptedProcess(
            [
                """{"type":"thread.started","thread_id":"thread-late"}""",
                """{"type":"turn.started"}""",
                """{"type":"turn.completed"}""",
            ],
            exitCode: 0));
        var adapter = CreateAdapter(factory);
        var sink = new RecordingHandleSink();
        var ct = TestContext.Current.CancellationToken;

        var receipt = await adapter.StartAsync(StartRequest(), sink, ct);
        var result = await adapter.GetResultAsync(receipt.Handle, ct);
        result.State.Should().Be(ExternalOperationState.Succeeded);

        var cancel = await adapter.CancelAsync(
            new ExternalOperationCancelRequest(receipt.Handle, "cancel-late", "stop"), ct);

        cancel.Disposition.Should().Be(ExternalOperationCancellationDisposition.AlreadyTerminal);
        cancel.State.Should().Be(ExternalOperationState.Succeeded);

        var status = await adapter.ObserveAsync(receipt.Handle, ct);
        status.State.Should().Be(ExternalOperationState.Succeeded);
        var again = await adapter.GetResultAsync(receipt.Handle, ct);
        again.Summary.Should().Be(result.Summary);
    }

    [Fact]
    public async Task Late_cancel_preserves_failure()
    {
        var factory = new ScriptedProcessFactory((_, _) =>
            new ScriptedProcess(RecordedUsageLimitProbe, exitCode: 1));
        var adapter = CreateAdapter(factory);
        var sink = new RecordingHandleSink();
        var ct = TestContext.Current.CancellationToken;

        var receipt = await adapter.StartAsync(StartRequest(), sink, ct);
        var failed = () => adapter.GetResultAsync(receipt.Handle, ct).AsTask();
        (await failed.Should().ThrowAsync<ExternalOperationProviderException>())
            .Which.Failure.Code.Should().Be("codex.usage-limit");

        var cancel = await adapter.CancelAsync(
            new ExternalOperationCancelRequest(receipt.Handle, "cancel-late", "stop"), ct);

        cancel.Disposition.Should().Be(ExternalOperationCancellationDisposition.AlreadyTerminal);
        cancel.State.Should().Be(ExternalOperationState.Failed);

        var status = await adapter.ObserveAsync(receipt.Handle, ct);
        status.State.Should().Be(ExternalOperationState.Failed);
        status.Failure!.Code.Should().Be("codex.usage-limit");
        (await failed.Should().ThrowAsync<ExternalOperationProviderException>())
            .Which.Failure.Code.Should().Be("codex.usage-limit");
    }

    [Fact]
    public async Task Concurrent_resumes_launch_once()
    {
        var factory = new ScriptedProcessFactory((invocation, _) =>
        {
            invocation.Arguments.Should().Contain("resume");
            return new ScriptedProcess(
                ["""{"type":"thread.started","thread_id":"thread-c"}"""],
                exitCode: 0,
                hangAfterLines: true);
        });
        var adapter = CreateAdapter(factory);
        var sink = new RecordingHandleSink();
        var ct = TestContext.Current.CancellationToken;
        var handle = TrackedHandle("thread-c");

        var resumes = await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
            adapter.ResumeAsync(new ExternalOperationResumeRequest(handle, $"resume-{index}"), sink, ct).AsTask()));

        factory.Spawns.Should().Be(1);
        resumes.Should().OnlyContain(resume =>
            resume.Disposition == ExternalOperationStartDisposition.Existing && resume.Handle == handle);
        sink.Captures.Should().HaveCount(8);
    }

    [Fact]
    public async Task Mismatched_correlation_rejected()
    {
        var factory = new ScriptedProcessFactory((_, _) => new ScriptedProcess(
            [
                """{"type":"thread.started","thread_id":"thread-1"}""",
                """{"type":"turn.started"}""",
            ],
            exitCode: 0,
            hangAfterLines: true));
        var adapter = CreateAdapter(factory);
        var sink = new RecordingHandleSink();
        var ct = TestContext.Current.CancellationToken;

        var receipt = await adapter.StartAsync(StartRequest(), sink, ct);

        var generationMismatch = TrackedHandle(
            "thread-1",
            generation: new NodeGenerationId(Guid.Parse("00000000-0000-0000-0000-000000000099")));
        var observe = () => adapter.ObserveAsync(generationMismatch, ct).AsTask();
        (await observe.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*does not match*");
        var result = () => adapter.GetResultAsync(generationMismatch, ct).AsTask();
        await result.Should().ThrowAsync<InvalidOperationException>();
        var cancel = () => adapter.CancelAsync(
            new ExternalOperationCancelRequest(generationMismatch, "cancel-1", "stop"), ct).AsTask();
        await cancel.Should().ThrowAsync<InvalidOperationException>();

        var otherAgent = new ExternalAgentReference("other", "codex-agent", "codex.exec.v1");
        var providerMismatch = TrackedHandle("thread-1", agent: otherAgent);
        var providerObserve = () => adapter.ObserveAsync(providerMismatch, ct).AsTask();
        await providerObserve.Should().ThrowAsync<InvalidOperationException>();

        // The correctly correlated handle keeps working.
        var status = await adapter.ObserveAsync(receipt.Handle, ct);
        status.State.Should().Be(ExternalOperationState.Running);
    }

    [Fact]
    public async Task Cancel_key_conflict_fails_but_replay_succeeds()
    {
        var factory = new ScriptedProcessFactory((_, _) => new ScriptedProcess(
            ["""{"type":"thread.started","thread_id":"thread-keys"}"""],
            exitCode: 0,
            hangAfterLines: true,
            releaseOnKill: false));
        var adapter = CreateAdapter(factory);
        var sink = new RecordingHandleSink();
        var ct = TestContext.Current.CancellationToken;

        var receipt = await adapter.StartAsync(StartRequest(), sink, ct);
        var process = factory.Processes.Should().ContainSingle().Which;

        // Hold the pump behind the kill: the first intent is live but not yet
        // confirmed, so a different key deterministically conflicts.
        var first = adapter.CancelAsync(
            new ExternalOperationCancelRequest(receipt.Handle, "cancel-1", "stop"), ct).AsTask();
        await process.KilledTask.WaitAsync(ct);

        var conflict = () => adapter.CancelAsync(
            new ExternalOperationCancelRequest(receipt.Handle, "cancel-2", "stop"), ct).AsTask();
        var failure = (await conflict.Should().ThrowAsync<ExternalOperationProviderException>()).Which.Failure;
        failure.Code.Should().Be("codex.cancel-conflict");
        failure.Kind.Should().Be(ExternalOperationFailureKind.Rejection);

        process.ReleaseReads();
        var requested = await first;
        requested.Disposition.Should().Be(ExternalOperationCancellationDisposition.Requested);

        var replay = await adapter.CancelAsync(
            new ExternalOperationCancelRequest(receipt.Handle, "cancel-1", "stop"), ct);
        replay.Should().BeSameAs(requested);

        var status = await adapter.ObserveAsync(receipt.Handle, ct);
        status.State.Should().Be(ExternalOperationState.Cancelled);
    }

    [Fact]
    public async Task Start_key_conflict_fails()
    {
        var factory = new ScriptedProcessFactory((_, _) => new ScriptedProcess(
            [],
            exitCode: 0,
            hangAfterLines: true));
        var adapter = CreateAdapter(factory);
        var sink = new RecordingHandleSink();
        var ct = TestContext.Current.CancellationToken;

        var first = adapter.StartAsync(StartRequest(), sink, ct).AsTask();
        var conflict = () => adapter.StartAsync(StartRequest(fingerprint: Hash2), sink, ct).AsTask();
        var failure = (await conflict.Should().ThrowAsync<ExternalOperationProviderException>()).Which.Failure;
        failure.Code.Should().Be("codex.start-conflict");
        failure.Kind.Should().Be(ExternalOperationFailureKind.Rejection);
        factory.Spawns.Should().Be(1);

        // Release the orphaned first launch and observe its failure.
        factory.Processes.Should().ContainSingle().Which.Kill();
        var orphaned = () => first;
        await orphaned.Should().ThrowAsync<ExternalOperationProviderException>();
    }

    [Fact]
    public async Task Identical_concurrent_start_attaches_without_respawn()
    {
        var factory = new ScriptedProcessFactory((_, _) => new ScriptedProcess(
            ["""{"type":"thread.started","thread_id":"thread-a"}"""],
            exitCode: 0,
            hangAfterLines: true));
        var adapter = CreateAdapter(factory);
        var sink = new RecordingHandleSink();
        var ct = TestContext.Current.CancellationToken;

        var first = await adapter.StartAsync(StartRequest(), sink, ct);
        first.Disposition.Should().Be(ExternalOperationStartDisposition.Created);
        var second = await adapter.StartAsync(StartRequest(), sink, ct);

        second.Disposition.Should().Be(ExternalOperationStartDisposition.Existing);
        second.Handle.Should().Be(first.Handle);
        factory.Spawns.Should().Be(1);
        sink.Captures.Should().HaveCount(2);

        var cancel = await adapter.CancelAsync(
            new ExternalOperationCancelRequest(first.Handle, "cancel-1", "stop"), ct);
        cancel.Disposition.Should().Be(ExternalOperationCancellationDisposition.Requested);
        var status = await adapter.ObserveAsync(first.Handle, ct);
        status.State.Should().Be(ExternalOperationState.Cancelled);
    }

    [Fact]
    public async Task Kill_exit_race_yields_single_terminal_result()
    {
        var factory = new ScriptedProcessFactory((_, _) => new ScriptedProcess(
            [
                """{"type":"thread.started","thread_id":"thread-race"}""",
                """{"type":"turn.started"}""",
            ],
            exitCode: 0));
        var adapter = CreateAdapter(factory);
        var sink = new RecordingHandleSink();
        var ct = TestContext.Current.CancellationToken;

        var receipt = await adapter.StartAsync(StartRequest(), sink, ct);
        // Either receipt is valid: the race decides whether cancellation met
        // live work or an already-terminal outcome. Exactly one outcome must
        // survive, observably stable across reads.
        _ = await adapter.CancelAsync(
            new ExternalOperationCancelRequest(receipt.Handle, "cancel-race", "stop"), ct);

        var first = await adapter.ObserveAsync(receipt.Handle, ct);
        var second = await adapter.ObserveAsync(receipt.Handle, ct);
        second.State.Should().Be(first.State);
        first.State.Should().BeOneOf(
            ExternalOperationState.Succeeded,
            ExternalOperationState.Cancelled,
            ExternalOperationState.Failed);

        switch (first.State)
        {
            case ExternalOperationState.Succeeded:
                (await adapter.GetResultAsync(receipt.Handle, ct)).State
                    .Should().Be(ExternalOperationState.Succeeded);
                break;
            case ExternalOperationState.Cancelled:
                var cancelled = () => adapter.GetResultAsync(receipt.Handle, ct).AsTask();
                (await cancelled.Should().ThrowAsync<ExternalOperationProviderException>())
                    .Which.Failure.Code.Should().Be("codex.cancelled");
                break;
            default:
                var failed = () => adapter.GetResultAsync(receipt.Handle, ct).AsTask();
                await failed.Should().ThrowAsync<ExternalOperationProviderException>();
                break;
        }

        var process = factory.Processes.Should().ContainSingle().Which;
        await WaitUntilAsync(() => process.Disposed, ct);
    }

    [Fact]
    public async Task StartAsync_refuses_workspace_that_escaped_after_admission()
    {
        var parent = Path.Combine(Path.GetTempPath(), "qh03-" + Guid.NewGuid().ToString("N"));
        var root = Directory.CreateDirectory(Path.Combine(parent, "root")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(parent, "outside")).FullName;
        try
        {
            var inside = Directory.CreateDirectory(Path.Combine(root, "real")).FullName;
            var link = Path.Combine(root, "link");
            try
            {
                Directory.CreateSymbolicLink(link, inside);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                Assert.Skip($"Symlink creation is not permitted here; the launch hook relies on QH-02 units. ({exception.GetType().Name})");
                return;
            }

            var adapter = new CodexExecAdapter(
                new CodexExecOptions("do the work", Path.Combine(link, "sub"), root),
                new ScriptedProcessFactory((_, _) => new ScriptedProcess(
                    ["""{"type":"thread.started","thread_id":"thread-swap"}"""],
                    exitCode: 0)));
            var sink = new RecordingHandleSink();
            var ct = TestContext.Current.CancellationToken;

            Directory.Delete(link);
            Directory.CreateSymbolicLink(link, outside);

            var act = () => adapter.StartAsync(StartRequest(), sink, ct).AsTask();
            var failure = (await act.Should().ThrowAsync<ExternalOperationProviderException>()).Which.Failure;
            failure.Code.Should().Be("codex.workspace-refused");
            failure.Kind.Should().Be(ExternalOperationFailureKind.Transport);
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    private static string WorkspaceRoot() =>
        Path.Combine(Path.GetTempPath(), "qingniao-codex-tests");

    private static string Workspace() => Path.Combine(WorkspaceRoot(), "work");

    private static CodexExecAdapter CreateAdapter(ScriptedProcessFactory factory, string prompt = "do the work") =>
        new(new CodexExecOptions(prompt, Workspace(), WorkspaceRoot()), factory);

    private static ExternalOperationStartRequest StartRequest(string fingerprint = Hash, string startKey = "start-key-1", DateTimeOffset? deadline = null) => new(
        new ExternalOperationStartIdentity(Delegation, Workflow, Node, Generation, "attempt-1", startKey, fingerprint),
        new ExternalOperationCorrelation(Delegation, Workflow, Node, Generation, "attempt-1", Agent, task: null),
        "agent.execute",
        [],
        budget: null,
        deadline: deadline);

    private static ExternalOperationHandle TrackedHandle(
        string threadId,
        NodeGenerationId? generation = null,
        ExternalAgentReference? agent = null)
    {
        var resolvedAgent = agent ?? Agent;
        var task = new ExternalTaskReference(resolvedAgent.Provider, threadId);
        var correlation = new ExternalOperationCorrelation(
            Delegation, Workflow, Node, generation ?? Generation, "attempt-1", resolvedAgent, task);
        return new ExternalOperationHandle(resolvedAgent.Provider, threadId, resolvedAgent.ProtocolVersion, correlation);
    }

    private sealed class RecordingHandleSink : IExternalOperationHandleCaptureSink
    {
        public List<ExternalOperationHandleCapture> Captures { get; } = [];

        public ValueTask CaptureAsync(ExternalOperationHandleCapture capture, CancellationToken cancellationToken = default)
        {
            Captures.Add(capture);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FlakyHandleSink(Func<int, bool> failOnCall) : IExternalOperationHandleCaptureSink
    {
        private int calls;

        public List<ExternalOperationHandleCapture> Captures { get; } = [];

        public ValueTask CaptureAsync(ExternalOperationHandleCapture capture, CancellationToken cancellationToken = default)
        {
            calls++;
            if (failOnCall(calls))
            {
                throw new InvalidOperationException("Response lost.");
            }

            Captures.Add(capture);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScriptedProcessFactory(
        Func<CodexProcessInvocation, ScriptedProcessFactory, ScriptedProcess> script) : ICodexProcessFactory
    {
        public int Spawns { get; private set; }

        public List<ScriptedProcess> Processes { get; } = [];

        public ICodexProcess Start(CodexProcessInvocation invocation)
        {
            Spawns++;
            var process = script(invocation, this);
            Processes.Add(process);
            return process;
        }
    }

    private sealed class ScriptedProcess(
        IReadOnlyList<string> lines,
        int exitCode,
        bool hangAfterLines = false,
        IReadOnlyList<string>? stderrLines = null,
        Exception? readFailure = null,
        bool releaseOnKill = true) : ICodexProcess
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource killedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Killed { get; private set; }

        public bool Disposed { get; private set; }

        public bool StderrDrained { get; private set; }

        public Task KilledTask => killedSignal.Task;

        public void ReleaseReads() => release.TrySetResult();

        public async IAsyncEnumerable<string> ReadLinesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (readFailure is not null)
            {
                throw readFailure;
            }

            foreach (var line in lines)
            {
                yield return line;
            }

            if (hangAfterLines)
            {
                await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public async IAsyncEnumerable<string> ReadErrorLinesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var line in stderrLines ?? [])
            {
                yield return line;
            }

            StderrDrained = true;
            if (hangAfterLines)
            {
                await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
        {
            if (hangAfterLines)
            {
                await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return exitCode;
        }

        public void Kill()
        {
            Killed = true;
            killedSignal.TrySetResult();
            if (releaseOnKill)
            {
                release.TrySetResult();
            }
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
