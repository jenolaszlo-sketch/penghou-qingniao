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
    public async Task StartAsync_completes_minimal_observed_lifecycle()
    {
        var factory = new ScriptedProcessFactory((_, _) => new ScriptedProcess(
            [
                """{"type":"thread.started","thread_id":"thread-1"}""",
                """{"type":"turn.started"}""",
            ],
            exitCode: 0));
        var adapter = CreateAdapter(factory);
        var sink = new RecordingHandleSink();
        var ct = TestContext.Current.CancellationToken;

        var receipt = await adapter.StartAsync(StartRequest(), sink, ct);
        receipt.State.Should().Be(ExternalOperationState.Running);

        var result = await adapter.GetResultAsync(receipt.Handle, ct);
        result.State.Should().Be(ExternalOperationState.Succeeded);
        result.Handle.Should().Be(receipt.Handle);
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

    private static string WorkspaceRoot() =>
        Path.Combine(Path.GetTempPath(), "qingniao-codex-tests");

    private static string Workspace() => Path.Combine(WorkspaceRoot(), "work");

    private static CodexExecAdapter CreateAdapter(ScriptedProcessFactory factory, string prompt = "do the work") =>
        new(new CodexExecOptions(prompt, Workspace(), WorkspaceRoot()), factory);

    private static ExternalOperationStartRequest StartRequest() => new(
        new ExternalOperationStartIdentity(Delegation, Workflow, Node, Generation, "attempt-1", "start-key-1", Hash),
        new ExternalOperationCorrelation(Delegation, Workflow, Node, Generation, "attempt-1", Agent, task: null),
        "agent.execute",
        []);

    private static ExternalOperationHandle TrackedHandle(string threadId)
    {
        var task = new ExternalTaskReference(Agent.Provider, threadId);
        var correlation = new ExternalOperationCorrelation(
            Delegation, Workflow, Node, Generation, "attempt-1", Agent, task);
        return new ExternalOperationHandle(Agent.Provider, threadId, Agent.ProtocolVersion, correlation);
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
        bool hangAfterLines = false) : ICodexProcess
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Killed { get; private set; }

        public async IAsyncEnumerable<string> ReadLinesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var line in lines)
            {
                yield return line;
            }

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
            release.TrySetResult();
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
