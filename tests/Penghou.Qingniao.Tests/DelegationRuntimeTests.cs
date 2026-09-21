using FluentAssertions;

namespace Penghou.Qingniao.Tests;

/// <summary>
/// Exercises the public <see cref="DelegationRuntime"/> façade only: no
/// internal coordinator, runner, or store types appear here. If Marang can
/// do everything this file does, the runtime is consumable.
/// </summary>
public sealed class DelegationRuntimeTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Delegate_pumps_to_completed_through_the_public_surface_only()
    {
        var provider = new FacadeProvider();
        var runtime = CreateRuntime(provider);
        var caller = new DelegationCallerScope("facade-caller");
        var request = CreateRequest("facade-run");
        var ct = TestContext.Current.CancellationToken;

        var handle = await runtime.DelegateAsync(caller, request, ct);

        handle.DelegationId.Should().NotBe(default(DelegationId));
        handle.Workflow.Provider.Should().Be("qingniao");
        handle.State.Should().Be(DelegationState.Queued);

        var terminal = await PumpToTerminalAsync(runtime, handle.DelegationId, ct);

        terminal.Progress.State.Should().Be(DelegationState.Completed);
        terminal.Result.Should().NotBeNull();
        (await runtime.GetStatusAsync(handle.DelegationId, ct))!.State.Should().Be(DelegationState.Completed);
        (await runtime.GetResultAsync(handle.DelegationId, ct))!.Should().BeSameAs(terminal.Result);
    }

    [Fact]
    public async Task Unknown_delegation_reads_as_null_and_cancel_throws()
    {
        var runtime = CreateRuntime(new FacadeProvider());
        var unknown = DelegationId.New();
        var ct = TestContext.Current.CancellationToken;

        (await runtime.GetStatusAsync(unknown, ct)).Should().BeNull();
        (await runtime.GetResultAsync(unknown, ct)).Should().BeNull();

        var act = () => runtime.CancelAsync(unknown, ct);
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*Unknown delegation*");
    }

    [Fact]
    public async Task Service_cancel_terminates_before_execution()
    {
        var runtime = CreateRuntime(new FacadeProvider());
        var ct = TestContext.Current.CancellationToken;
        var handle = await runtime.DelegateAsync(new DelegationCallerScope("caller"), CreateRequest("cancel-me"), ct);

        await runtime.CancelAsync(handle.DelegationId, ct);
        var status = await runtime.GetStatusAsync(handle.DelegationId, ct);

        status!.State.Should().Be(DelegationState.Cancelled);
    }

    [Fact]
    public async Task Admission_rejection_surfaces_through_delegate()
    {
        var runtime = CreateRuntime(
            new FacadeProvider(),
            new RejectingAdmissionVerifier(DelegationAdmissionStatus.Unauthorized, "not this caller"));
        var ct = TestContext.Current.CancellationToken;

        var act = () => runtime.DelegateAsync(
            new DelegationCallerScope("caller"),
            CreateRequest("rejected"),
            ct);

        (await act.Should().ThrowAsync<DelegationAdmissionException>())
            .Which.Status.Should().Be(DelegationAdmissionStatus.Unauthorized);
    }

    [Fact]
    public async Task Missing_adapter_needs_supervision_with_an_honest_summary()
    {
        var runtime = CreateRuntime(new FacadeProvider(), registerAdapter: false);
        var ct = TestContext.Current.CancellationToken;
        var handle = await runtime.DelegateAsync(new DelegationCallerScope("caller"), CreateRequest("no-adapter"), ct);

        var terminal = await PumpToTerminalAsync(runtime, handle.DelegationId, ct);

        terminal.Progress.State.Should().Be(DelegationState.NeedsSupervisor);
        terminal.Result!.Summary.Should().Contain("adapter");
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
        "facade-provider",
        new WorkspaceReference("local", "workspace", "revision"),
        ["Done"],
        [],
        new DelegationBudget(MaximumWorkerCalls: 8, MaximumRetries: 2));

    private static DelegationRuntime CreateRuntime(
        FacadeProvider provider,
        IDelegationAdmissionVerifier? verifier = null,
        bool registerAdapter = true)
    {
        var descriptor = new ProviderDescriptor("facade-provider", [new CapabilityDescriptor("agent.execute", 1)]);
        var providers = new InMemoryProviderRegistry();
        providers.Register(descriptor);
        var adapters = new InMemoryExternalOperationProviderCatalog();
        if (registerAdapter)
        {
            adapters.Register(descriptor, provider);
        }

        return new DelegationRuntime(
            new InMemoryDelegationAcceptanceRegistry(),
            verifier,
            providers,
            adapters,
            now: () => Start);
    }

    private sealed class RejectingAdmissionVerifier(
        DelegationAdmissionStatus status,
        string reason) : IDelegationAdmissionVerifier
    {
        public DelegationAdmissionDecision Verify(DelegationAdmissionContext context) =>
            DelegationAdmissionDecision.Reject(status, reason);
    }

    private sealed class FacadeProvider : IExternalOperationProvider
    {
        private ExternalOperationHandle? handle;

        public async ValueTask<ExternalOperationStartReceipt> StartAsync(
            ExternalOperationStartRequest request,
            IExternalOperationHandleCaptureSink handleSink,
            CancellationToken cancellationToken = default)
        {
            var correlation = new ExternalOperationCorrelation(
                request.Correlation.DelegationId,
                request.Correlation.WorkflowRun,
                request.Correlation.StructuralNode,
                request.Correlation.NodeGeneration,
                request.Correlation.ExecutionAttemptId,
                request.Correlation.Agent,
                new ExternalTaskReference(request.Correlation.Agent.Provider, "facade-task"));
            handle = new ExternalOperationHandle(
                request.Correlation.Agent.Provider,
                "facade-handle",
                request.Correlation.Agent.ProtocolVersion,
                correlation);
            await handleSink.CaptureAsync(new ExternalOperationHandleCapture(handle, Start.AddMinutes(1)), cancellationToken);
            return new ExternalOperationStartReceipt(
                request.Identity, handle, ExternalOperationStartDisposition.Created, ExternalOperationState.Running, Start.AddMinutes(1));
        }

        public ValueTask<ExternalOperationObservation> ObserveAsync(
            ExternalOperationHandle operationHandle,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ExternalOperationObservation(
                operationHandle, 1, ExternalOperationState.Succeeded, Start.AddMinutes(2), resultAvailable: true));

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
    }
}
