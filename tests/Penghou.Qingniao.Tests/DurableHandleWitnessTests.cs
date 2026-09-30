using FluentAssertions;

namespace Penghou.Qingniao.Tests;

/// <summary>
/// QH-08 prep: proves the durable handle-witness seam through the public
/// <see cref="DelegationRuntime"/> surface. The witness receives every accepted
/// capture before observation, and a witness failure fails the start closed
/// without observing or duplicating provider work.
/// </summary>
public sealed class DurableHandleWitnessTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Witness_receives_start_capture_before_first_observe()
    {
        var provider = new WitnessProvider();
        var witness = new RecordingWitness(() => provider.ObserveCalls);
        var runtime = CreateRuntime(provider, witness);
        var ct = TestContext.Current.CancellationToken;
        var handle = await runtime.DelegateAsync(new DelegationCallerScope("caller"), CreateRequest("witness-order"), ct);

        var terminal = await PumpToTerminalAsync(runtime, handle.DelegationId, ct);

        terminal.Progress.State.Should().Be(DelegationState.Completed);
        witness.Captures.Should().ContainSingle()
            .Which.Handle.Value.Should().Be("witness-handle");
        witness.ObserveCallsAtCapture.Should().ContainSingle().Which.Should().Be(0);
        provider.ObserveCalls.Should().BePositive();
    }

    [Fact]
    public async Task Witness_failure_fails_start_closed_without_observe_or_duplicate_start()
    {
        var provider = new WitnessProvider();
        var runtime = CreateRuntime(provider, new ThrowingWitness());
        var ct = TestContext.Current.CancellationToken;
        var handle = await runtime.DelegateAsync(new DelegationCallerScope("caller"), CreateRequest("witness-fails"), ct);

        var queued = await runtime.GetAsync(handle.DelegationId, ct);
        var terminal = await runtime.PumpAsync(handle.DelegationId, queued.Progress.Revision, ct);

        terminal.Progress.State.Should().Be(DelegationState.Failed);
        terminal.Result!.Summary.Should().Contain("receipt validation failed");
        provider.StartCalls.Should().Be(1);
        provider.ObserveCalls.Should().Be(0);

        var replay = await runtime.PumpAsync(handle.DelegationId, terminal.Progress.Revision, ct);
        replay.Progress.State.Should().Be(DelegationState.Failed);
        provider.StartCalls.Should().Be(1);
        provider.ObserveCalls.Should().Be(0);
    }

    [Fact]
    public async Task Witness_receives_resume_rotation_capture_in_order()
    {
        var provider = new RotatingProvider();
        var witness = new RecordingWitness(() => 0);
        var runtime = CreateRuntime(provider, witness);
        var ct = TestContext.Current.CancellationToken;
        var handle = await runtime.DelegateAsync(new DelegationCallerScope("caller"), CreateRequest("witness-rotation"), ct);

        var queued = await runtime.GetAsync(handle.DelegationId, ct);
        var running = await runtime.PumpAsync(handle.DelegationId, queued.Progress.Revision, ct);
        var waiting = await runtime.PumpAsync(handle.DelegationId, running.Progress.Revision, ct);
        waiting.Progress.State.Should().Be(DelegationState.WaitingForSupervisor);

        var supervisor = new SupervisorIdentity("host", "operator");
        await runtime.ActivateCheckpointAsync(handle.DelegationId, supervisor, ct);
        var intervention = new SupervisorIntervention(
            handle.DelegationId,
            waiting.Progress.Checkpoint!.CheckpointId,
            "witness-approve",
            waiting.Progress.Revision,
            new SupervisorAction.Approve("continue"));
        var resumed = await runtime.ApplyInterventionAsync(handle.DelegationId, supervisor, intervention, ct);

        resumed.Progress.State.Should().Be(DelegationState.Running);
        provider.ResumeCalls.Should().Be(1);
        witness.Captures.Select(capture => capture.Handle.Value).Should()
            .ContainInOrder("witness-initial", "witness-rotated");
    }

    private static async Task<DelegationExecutionSnapshot> PumpToTerminalAsync(
        DelegationRuntime runtime,
        DelegationId id,
        CancellationToken ct)
    {
        var snapshot = await runtime.GetAsync(id, ct);
        for (var index = 0; index < 10 && !DelegationLifecycle.IsTerminal(snapshot.Progress.State); index++)
        {
            snapshot = await runtime.PumpAsync(id, snapshot.Progress.Revision, ct);
        }

        DelegationLifecycle.IsTerminal(snapshot.Progress.State).Should().BeTrue();
        return snapshot;
    }

    private static DelegationRequest CreateRequest(string requestKey) => new(
        requestKey,
        "Do the delegated work",
        "witness-provider",
        new WorkspaceReference("local", "workspace", "revision"),
        ["Done"],
        [],
        new DelegationBudget(MaximumWorkerCalls: 8, MaximumRetries: 2));

    private static DelegationRuntime CreateRuntime(
        IExternalOperationProvider provider,
        IExternalOperationHandleCaptureSink witness)
    {
        var descriptor = new ProviderDescriptor("witness-provider", [new CapabilityDescriptor("agent.execute", 1)]);
        var providers = new InMemoryProviderRegistry();
        providers.Register(descriptor);
        var adapters = new InMemoryExternalOperationProviderCatalog();
        adapters.Register(descriptor, provider);

        return new DelegationRuntime(
            new InMemoryDelegationAcceptanceRegistry(),
            null,
            providers,
            adapters,
            now: () => Start,
            durableHandleWitness: witness);
    }

    private sealed class RecordingWitness(Func<int> observeCalls) : IExternalOperationHandleCaptureSink
    {
        private readonly List<ExternalOperationHandleCapture> captures = [];
        private readonly List<int> observeCallsAtCapture = [];

        public IReadOnlyList<ExternalOperationHandleCapture> Captures => captures;
        public IReadOnlyList<int> ObserveCallsAtCapture => observeCallsAtCapture;

        public ValueTask CaptureAsync(ExternalOperationHandleCapture capture, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(capture);
            captures.Add(capture);
            observeCallsAtCapture.Add(observeCalls());
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingWitness : IExternalOperationHandleCaptureSink
    {
        public ValueTask CaptureAsync(ExternalOperationHandleCapture capture, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new InvalidOperationException("Durable store unavailable."));
    }

    private sealed class WitnessProvider : IExternalOperationProvider
    {
        public int StartCalls { get; private set; }
        public int ObserveCalls { get; private set; }

        public ValueTask<ExternalOperationStartReceipt> StartAsync(
            ExternalOperationStartRequest request,
            IExternalOperationHandleCaptureSink handleSink,
            CancellationToken cancellationToken = default)
        {
            StartCalls++;
            var handle = Handle(request, "witness-handle");
            return ValueTask.FromResult(new ExternalOperationStartReceipt(
                request.Identity, handle, ExternalOperationStartDisposition.Created, ExternalOperationState.Running, Start.AddMinutes(1)));
        }

        public ValueTask<ExternalOperationObservation> ObserveAsync(
            ExternalOperationHandle operationHandle,
            CancellationToken cancellationToken = default)
        {
            ObserveCalls++;
            return ValueTask.FromResult(new ExternalOperationObservation(
                operationHandle, ObserveCalls, ExternalOperationState.Succeeded, Start.AddMinutes(2), resultAvailable: true));
        }

        public ValueTask<ExternalOperationResult> GetResultAsync(
            ExternalOperationHandle operationHandle,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ExternalOperationResult(
                operationHandle, ExternalOperationState.Succeeded, Start.AddMinutes(3), "done", []));

        public ValueTask<ExternalOperationCancellationReceipt> CancelAsync(
            ExternalOperationCancelRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ExternalOperationCancellationReceipt(
                request.Handle, request.CancellationKey, ExternalOperationCancellationDisposition.Requested,
                ExternalOperationState.CancellationRequested, Start.AddMinutes(4)));

        public ValueTask<ExternalOperationResumeReceipt> ResumeAsync(
            ExternalOperationResumeRequest request,
            IExternalOperationHandleCaptureSink handleSink,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        private static ExternalOperationHandle Handle(ExternalOperationStartRequest request, string value) => new(
            request.Correlation.Agent.Provider, value, request.Correlation.Agent.ProtocolVersion,
            new ExternalOperationCorrelation(request.Correlation.DelegationId, request.Correlation.WorkflowRun,
                request.Correlation.StructuralNode, request.Correlation.NodeGeneration,
                request.Correlation.ExecutionAttemptId, request.Correlation.Agent,
                new ExternalTaskReference(request.Correlation.Agent.Provider, value)));
    }

    private sealed class RotatingProvider : IExternalOperationProvider
    {
        public int ResumeCalls { get; private set; }

        public async ValueTask<ExternalOperationStartReceipt> StartAsync(
            ExternalOperationStartRequest request,
            IExternalOperationHandleCaptureSink handleSink,
            CancellationToken cancellationToken = default)
        {
            var handle = Handle(request, "witness-initial");
            await handleSink.CaptureAsync(new ExternalOperationHandleCapture(handle, Start.AddMinutes(1)), cancellationToken);
            return new ExternalOperationStartReceipt(
                request.Identity, handle, ExternalOperationStartDisposition.Created, ExternalOperationState.Running, Start.AddMinutes(1));
        }

        public ValueTask<ExternalOperationObservation> ObserveAsync(
            ExternalOperationHandle operationHandle,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ExternalOperationObservation(
                operationHandle, 1, ExternalOperationState.Waiting, Start.AddMinutes(2), providerStatus: "needs approval"));

        public ValueTask<ExternalOperationResult> GetResultAsync(
            ExternalOperationHandle operationHandle,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<ExternalOperationCancellationReceipt> CancelAsync(
            ExternalOperationCancelRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async ValueTask<ExternalOperationResumeReceipt> ResumeAsync(
            ExternalOperationResumeRequest request,
            IExternalOperationHandleCaptureSink handleSink,
            CancellationToken cancellationToken = default)
        {
            ResumeCalls++;
            var rotated = Handle(request.Handle.Correlation, "witness-rotated");
            await handleSink.CaptureAsync(new ExternalOperationHandleCapture(rotated, Start.AddMinutes(4)), cancellationToken);
            return new ExternalOperationResumeReceipt(
                request.Handle, request.ResumeKey, rotated, ExternalOperationStartDisposition.Existing, ExternalOperationState.Running, Start.AddMinutes(4));
        }

        private static ExternalOperationHandle Handle(ExternalOperationStartRequest request, string value) =>
            Handle(request.Correlation, value);

        private static ExternalOperationHandle Handle(ExternalOperationCorrelation correlation, string value) => new(
            correlation.Agent.Provider, value, correlation.Agent.ProtocolVersion,
            new ExternalOperationCorrelation(correlation.DelegationId, correlation.WorkflowRun,
                correlation.StructuralNode, correlation.NodeGeneration,
                correlation.ExecutionAttemptId, correlation.Agent,
                new ExternalTaskReference(correlation.Agent.Provider, value)));
    }
}
