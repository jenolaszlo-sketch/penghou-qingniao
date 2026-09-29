using FluentAssertions;

namespace Penghou.Qingniao.Tests;

/// <summary>
/// QH-05 regressions: provider I/O never holds the per-runtime gate, so
/// cancellation intent and duration bounds stay observable through stalled
/// calls, and late provider outcomes reconcile under the revision fence
/// instead of overwriting newer state. All stalls use controlled gates and
/// clocks; no timing-sensitive sleeps.
/// </summary>
public sealed class ProviderStallTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Stalled_start_permits_recorded_cancellation_and_late_capture_reconciles()
    {
        var (coordinator, provider) = CreateCoordinator();
        var ct = TestContext.Current.CancellationToken;
        var acceptance = await coordinator.AcceptAsync(Caller(), CreateRequest("stalled-start"), ct);
        var queued = await coordinator.GetAsync(acceptance.DelegationId, ct);

        var pumpTask = coordinator.PumpAsync(acceptance.DelegationId, queued.Progress.Revision, ct).AsTask();
        await WaitUntilAsync(() => provider.StartCalls == 1, ct);

        // The gate is free while the start call is pending: cancellation is
        // recorded promptly instead of waiting for the stalled provider.
        using var cancelTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cancelTimeout.Token);
        var cancelled = await coordinator.CancelAsync(
            acceptance.DelegationId,
            (await coordinator.GetAsync(acceptance.DelegationId, ct)).Progress.Revision,
            "cancel-1",
            "stop",
            linked.Token);

        // Worker-call accounting includes the started operation even while it
        // is still stalled inside the provider.
        cancelled.Progress.WorkerCalls.Should().Be(1);
        provider.StartCalls.Should().Be(1);

        // The late start receipt reconciles into the recorded cancellation
        // instead of launching fresh work.
        provider.CancelFactory = request => new ExternalOperationCancellationReceipt(
            request.Handle,
            request.CancellationKey,
            ExternalOperationCancellationDisposition.ConfirmedCancelled,
            ExternalOperationState.Cancelled,
            Start.AddMinutes(9));
        provider.ReleaseStart.TrySetResult();

        var terminal = await pumpTask;
        terminal.Progress.State.Should().Be(DelegationState.Cancelled);
        provider.SeenStarts.Should().ContainSingle();
        provider.CancelCalls.Should().Be(1);
    }

    [Fact]
    public async Task Stalled_observe_permits_recorded_cancellation()
    {
        var (coordinator, provider) = CreateCoordinator();
        provider.ReleaseStart.TrySetResult();
        var ct = TestContext.Current.CancellationToken;
        var acceptance = await coordinator.AcceptAsync(Caller(), CreateRequest("stalled-observe"), ct);
        var queued = await coordinator.GetAsync(acceptance.DelegationId, ct);
        var running = await coordinator.PumpAsync(acceptance.DelegationId, queued.Progress.Revision, ct);
        running.Progress.State.Should().Be(DelegationState.Running);

        var pumpTask = coordinator.PumpAsync(acceptance.DelegationId, running.Progress.Revision, ct).AsTask();
        await WaitUntilAsync(() => provider.ObserveCalls == 1, ct);

        using var cancelTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cancelTimeout.Token);
        provider.CancelFactory = request => new ExternalOperationCancellationReceipt(
            request.Handle,
            request.CancellationKey,
            ExternalOperationCancellationDisposition.ConfirmedCancelled,
            ExternalOperationState.Cancelled,
            Start.AddMinutes(9));
        var cancelled = await coordinator.CancelAsync(
            acceptance.DelegationId,
            (await coordinator.GetAsync(acceptance.DelegationId, ct)).Progress.Revision,
            "cancel-1",
            "stop",
            linked.Token);

        cancelled.Progress.State.Should().Be(DelegationState.Cancelled);
        provider.StartCalls.Should().Be(1);

        provider.ReleaseObserve.TrySetResult();
        var terminal = await pumpTask;
        terminal.Progress.State.Should().Be(DelegationState.Cancelled);
    }

    [Fact]
    public async Task Stalled_result_permits_recorded_cancellation()
    {
        var (coordinator, provider) = CreateCoordinator();
        provider.ReleaseStart.TrySetResult();
        provider.ReleaseObserve.TrySetResult();
        provider.ObserveFactory = (handle, calls) => new ExternalOperationObservation(
            handle,
            calls,
            ExternalOperationState.Succeeded,
            Start.AddMinutes(2),
            resultAvailable: true);
        var ct = TestContext.Current.CancellationToken;
        var acceptance = await coordinator.AcceptAsync(Caller(), CreateRequest("stalled-result"), ct);
        var queued = await coordinator.GetAsync(acceptance.DelegationId, ct);
        var started = await coordinator.PumpAsync(acceptance.DelegationId, queued.Progress.Revision, ct);
        var observed = await coordinator.PumpAsync(acceptance.DelegationId, started.Progress.Revision, ct);
        observed.Progress.State.Should().Be(DelegationState.Running);

        var pumpTask = coordinator.PumpAsync(acceptance.DelegationId, observed.Progress.Revision, ct).AsTask();
        await WaitUntilAsync(() => provider.ResultCalls == 1, ct);

        using var cancelTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cancelTimeout.Token);
        provider.CancelFactory = request => new ExternalOperationCancellationReceipt(
            request.Handle,
            request.CancellationKey,
            ExternalOperationCancellationDisposition.ConfirmedCancelled,
            ExternalOperationState.Cancelled,
            Start.AddMinutes(9));
        var cancelled = await coordinator.CancelAsync(
            acceptance.DelegationId,
            (await coordinator.GetAsync(acceptance.DelegationId, ct)).Progress.Revision,
            "cancel-1",
            "stop",
            linked.Token);

        cancelled.Progress.State.Should().Be(DelegationState.Cancelled);
        provider.StartCalls.Should().Be(1);

        provider.ReleaseResult.TrySetResult();
        var terminal = await pumpTask;
        terminal.Progress.State.Should().Be(DelegationState.Cancelled);
    }

    [Fact]
    public async Task Deadline_becomes_observable_during_stalled_start_without_duplicate_start()
    {
        var now = Start;
        var (coordinator, provider) = CreateCoordinator(() => now);
        var ct = TestContext.Current.CancellationToken;
        var acceptance = await coordinator.AcceptAsync(
            Caller(), CreateRequest("stalled-deadline", maximumDuration: TimeSpan.FromSeconds(10)), ct);
        var queued = await coordinator.GetAsync(acceptance.DelegationId, ct);

        var pumpTask = coordinator.PumpAsync(acceptance.DelegationId, queued.Progress.Revision, ct).AsTask();
        await WaitUntilAsync(() => provider.StartCalls == 1, ct);

        // A second pump observes the expired deadline while the first pump is
        // still stuck inside the provider: bounded execution never waits for
        // the stalled call.
        now = Start.AddSeconds(11);
        var current = await coordinator.GetAsync(acceptance.DelegationId, ct);
        var exceeded = await coordinator.PumpAsync(acceptance.DelegationId, current.Progress.Revision, ct);

        exceeded.Progress.State.Should().Be(DelegationState.BudgetExceeded);
        exceeded.Result!.BudgetExceeded.Should().NotBeNull();
        provider.StartCalls.Should().Be(1);

        // The stale late start cannot overwrite the terminal outcome and
        // never launches again.
        provider.ReleaseStart.TrySetResult();
        var late = await pumpTask;
        late.Progress.State.Should().Be(DelegationState.BudgetExceeded);
        provider.StartCalls.Should().Be(1);

        var replay = await coordinator.PumpAsync(acceptance.DelegationId, late.Progress.Revision, ct);
        replay.Progress.State.Should().Be(DelegationState.BudgetExceeded);
    }

    [Fact]
    public async Task Concurrent_pump_does_not_duplicate_stalled_start()
    {
        var (coordinator, provider) = CreateCoordinator();
        var ct = TestContext.Current.CancellationToken;
        var acceptance = await coordinator.AcceptAsync(Caller(), CreateRequest("stalled-concurrent"), ct);
        var queued = await coordinator.GetAsync(acceptance.DelegationId, ct);

        var first = coordinator.PumpAsync(acceptance.DelegationId, queued.Progress.Revision, ct).AsTask();
        await WaitUntilAsync(() => provider.StartCalls == 1, ct);

        var current = await coordinator.GetAsync(acceptance.DelegationId, ct);
        var second = await coordinator.PumpAsync(acceptance.DelegationId, current.Progress.Revision, ct);

        second.Progress.Revision.Should().Be(current.Progress.Revision);
        provider.StartCalls.Should().Be(1);

        provider.ReleaseStart.TrySetResult();
        var running = await first;
        running.Progress.State.Should().Be(DelegationState.Running);
        provider.StartCalls.Should().Be(1);
    }

    private static (InMemoryDelegationCoordinator Coordinator, GateProvider Provider) CreateCoordinator(
        Func<DateTimeOffset>? clock = null)
    {
        var provider = new GateProvider();
        var descriptor = new ProviderDescriptor(
            "fake-provider",
            [new CapabilityDescriptor("agent.execute", 1)]);
        var providers = new InMemoryProviderRegistry();
        providers.Register(descriptor);
        var adapters = new InMemoryExternalOperationProviderCatalog();
        adapters.Register(descriptor, provider);
        var coordinator = new InMemoryDelegationCoordinator(
            new InMemoryDelegationAcceptanceRegistry(),
            null,
            providers,
            adapters,
            new LocalSemanticFingerprintVerifier(),
            new InMemoryExternalOperationHandleCaptureRegistry(),
            null,
            now: clock ?? (() => Start));
        return (coordinator, provider);
    }

    private static DelegationCallerScope Caller() => new("caller-1");

    private static DelegationRequest CreateRequest(
        string requestKey,
        TimeSpan? maximumDuration = null) => new(
        requestKey,
        "Implement the objective",
        "fake-provider",
        new WorkspaceReference("local", "workspace", "revision"),
        ["The result is correct"],
        [],
        new DelegationBudget(MaximumWorkerCalls: 4, MaximumRetries: 1, MaximumDuration: maximumDuration));

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        while (!condition())
        {
            await Task.Delay(20, linked.Token);
        }
    }

    private sealed class GateProvider : IExternalOperationProvider
    {
        private static readonly DateTimeOffset AcceptedAt = Start.AddMinutes(1);
        private int threads;

        public int StartCalls;

        public int ObserveCalls;

        public int ResultCalls;

        public int CancelCalls;

        public List<ExternalOperationStartRequest> SeenStarts { get; } = [];

        public TaskCompletionSource ReleaseStart { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseObserve { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseResult { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Func<ExternalOperationHandle, int, ExternalOperationObservation>? ObserveFactory;

        public Func<ExternalOperationCancelRequest, ExternalOperationCancellationReceipt>? CancelFactory;

        public async ValueTask<ExternalOperationStartReceipt> StartAsync(
            ExternalOperationStartRequest request,
            IExternalOperationHandleCaptureSink handleSink,
            CancellationToken cancellationToken = default)
        {
            StartCalls++;
            SeenStarts.Add(request);
            await ReleaseStart.Task.WaitAsync(GateTimeout);
            var id = Interlocked.Increment(ref threads);
            var correlation = new ExternalOperationCorrelation(
                request.Correlation.DelegationId,
                request.Correlation.WorkflowRun,
                request.Correlation.StructuralNode,
                request.Correlation.NodeGeneration,
                request.Correlation.ExecutionAttemptId,
                request.Correlation.Agent,
                new ExternalTaskReference(request.Correlation.Agent.Provider, "gate-task-" + id));
            var handle = new ExternalOperationHandle(
                request.Correlation.Agent.Provider,
                "gate-thread-" + id,
                request.Correlation.Agent.ProtocolVersion,
                correlation);
            await handleSink.CaptureAsync(
                new ExternalOperationHandleCapture(handle, AcceptedAt),
                cancellationToken);
            return new ExternalOperationStartReceipt(
                request.Identity,
                handle,
                ExternalOperationStartDisposition.Created,
                ExternalOperationState.Running,
                AcceptedAt);
        }

        public async ValueTask<ExternalOperationObservation> ObserveAsync(
            ExternalOperationHandle operationHandle,
            CancellationToken cancellationToken = default)
        {
            ObserveCalls++;
            var calls = ObserveCalls;
            await ReleaseObserve.Task.WaitAsync(GateTimeout);
            if (ObserveFactory is not null)
            {
                return ObserveFactory(operationHandle, calls);
            }

            return new ExternalOperationObservation(
                operationHandle,
                calls,
                ExternalOperationState.Running,
                AcceptedAt.AddMinutes(calls),
                resultAvailable: false);
        }

        public async ValueTask<ExternalOperationResult> GetResultAsync(
            ExternalOperationHandle operationHandle,
            CancellationToken cancellationToken = default)
        {
            ResultCalls++;
            await ReleaseResult.Task.WaitAsync(GateTimeout);
            var artifact = new DelegationArtifactReference(
                operationHandle.Correlation.DelegationId,
                operationHandle.Correlation.StructuralNode,
                operationHandle.Correlation.NodeGeneration,
                "fake-provider",
                "fake-repository",
                "artifact-1",
                "text/plain",
                1,
                "memory://artifact-1",
                ArtifactContentIdentity.Sha256Bytes("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"));
            return new ExternalOperationResult(
                operationHandle,
                ExternalOperationState.Succeeded,
                AcceptedAt.AddMinutes(3),
                "completed",
                [artifact]);
        }

        public ValueTask<ExternalOperationCancellationReceipt> CancelAsync(
            ExternalOperationCancelRequest request,
            CancellationToken cancellationToken = default)
        {
            CancelCalls++;
            if (CancelFactory is not null)
            {
                return ValueTask.FromResult(CancelFactory(request));
            }

            return ValueTask.FromResult(new ExternalOperationCancellationReceipt(
                request.Handle,
                request.CancellationKey,
                ExternalOperationCancellationDisposition.Requested,
                ExternalOperationState.CancellationRequested,
                AcceptedAt));
        }

        public ValueTask<ExternalOperationResumeReceipt> ResumeAsync(
            ExternalOperationResumeRequest request,
            IExternalOperationHandleCaptureSink handleSink,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
