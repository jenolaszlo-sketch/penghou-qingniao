using FluentAssertions;

namespace Penghou.Qingniao.Tests;

/// <summary>
/// AE-6.2: every external provider start evaluates current start authority
/// immediately before the first provider side effect. Denial is terminal and
/// explicit with zero provider contact; unavailability performs nothing and
/// retries on a later pump; acceptance never substitutes for start authority.
/// </summary>
public sealed class ProviderStartAuthorizationTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Permitted_start_reaches_the_provider_with_exact_context()
    {
        var provider = new RecordingProvider();
        var gate = new SwitchableGate(ProviderStartAuthorizationStatus.Permitted);
        var runtime = CreateRuntime(provider, gate);
        var ct = TestContext.Current.CancellationToken;
        var handle = await runtime.DelegateAsync(new DelegationCallerScope("caller"), CreateRequest("permit"), ct);

        var terminal = await PumpToTerminalAsync(runtime, handle.DelegationId, ct);

        provider.StartRequests.Should().ContainSingle();
        var seen = gate.Contexts.Should().ContainSingle().Subject;
        seen.DelegationId.Should().Be(handle.DelegationId);
        seen.Workspace.Should().Be("workspace");
        seen.Provider.Should().Be("start-provider");
        seen.Revision.Should().BeGreaterOrEqualTo(0);
        terminal.Progress.State.Should().NotBe(DelegationState.AuthorityDenied);
    }

    [Fact]
    public async Task Denied_start_is_terminal_with_zero_provider_contact()
    {
        var provider = new RecordingProvider();
        var runtime = CreateRuntime(provider, TestProviderStartAuthorizer.DenyAll());
        var ct = TestContext.Current.CancellationToken;
        // Acceptance succeeds; the denial happens at start time.
        var handle = await runtime.DelegateAsync(new DelegationCallerScope("caller"), CreateRequest("deny"), ct);

        var terminal = await PumpToTerminalAsync(runtime, handle.DelegationId, ct);

        terminal.Progress.State.Should().Be(DelegationState.AuthorityDenied);
        provider.StartRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Unavailable_start_performs_nothing_and_reevaluates_on_retry()
    {
        var provider = new RecordingProvider();
        var gate = new SwitchableGate(ProviderStartAuthorizationStatus.Unavailable);
        var runtime = CreateRuntime(provider, gate);
        var ct = TestContext.Current.CancellationToken;
        var handle = await runtime.DelegateAsync(new DelegationCallerScope("caller"), CreateRequest("flaky"), ct);

        var first = await PumpOnceAsync(runtime, handle.DelegationId, ct);
        first.Progress.State.Should().Be(DelegationState.Queued);
        provider.StartRequests.Should().BeEmpty();

        // Authority narrows before the retry: the next pump re-evaluates.
        gate.Status = ProviderStartAuthorizationStatus.Denied;
        var terminal = await PumpToTerminalAsync(runtime, handle.DelegationId, ct);
        terminal.Progress.State.Should().Be(DelegationState.AuthorityDenied);
        provider.StartRequests.Should().BeEmpty();
        gate.Contexts.Should().HaveCountGreaterThan(1);
    }

    [Fact]
    public async Task Unavailable_then_permitted_starts_on_retry()
    {
        var provider = new RecordingProvider();
        var gate = new SwitchableGate(ProviderStartAuthorizationStatus.Unavailable);
        var runtime = CreateRuntime(provider, gate);
        var ct = TestContext.Current.CancellationToken;
        var handle = await runtime.DelegateAsync(new DelegationCallerScope("caller"), CreateRequest("recover"), ct);

        await PumpOnceAsync(runtime, handle.DelegationId, ct);
        gate.Status = ProviderStartAuthorizationStatus.Permitted;
        await PumpToTerminalAsync(runtime, handle.DelegationId, ct);

        provider.StartRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task Throwing_gate_fails_closed_without_start()
    {
        var provider = new RecordingProvider();
        var runtime = CreateRuntime(provider, new ThrowingGate());
        var ct = TestContext.Current.CancellationToken;
        var handle = await runtime.DelegateAsync(new DelegationCallerScope("caller"), CreateRequest("throw"), ct);

        var snapshot = await PumpOnceAsync(runtime, handle.DelegationId, ct);

        snapshot.Progress.State.Should().Be(DelegationState.Queued);
        provider.StartRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Null_outcome_gate_fails_closed_without_start()
    {
        var provider = new RecordingProvider();
        var runtime = CreateRuntime(provider, new NullGate());
        var ct = TestContext.Current.CancellationToken;
        var handle = await runtime.DelegateAsync(new DelegationCallerScope("caller"), CreateRequest("null"), ct);

        var snapshot = await PumpOnceAsync(runtime, handle.DelegationId, ct);

        snapshot.Progress.State.Should().Be(DelegationState.Queued);
        provider.StartRequests.Should().BeEmpty();
    }

    private static DelegationRequest CreateRequest(string requestKey) => new(
        requestKey,
        "Do the delegated work",
        "start-provider",
        new WorkspaceReference("local", "workspace", null),
        ["Done"],
        [],
        new DelegationBudget(MaximumWorkerCalls: 8, MaximumRetries: 2));

    private static DelegationRuntime CreateRuntime(
        RecordingProvider provider, IProviderStartAuthorizer gate)
    {
        var descriptor = new ProviderDescriptor("start-provider", [new CapabilityDescriptor("agent.execute", 1)]);
        var providers = new InMemoryProviderRegistry();
        providers.Register(descriptor);
        var adapters = new InMemoryExternalOperationProviderCatalog();
        adapters.Register(descriptor, provider);
        return new DelegationRuntime(
            new InMemoryDelegationAcceptanceRegistry(),
            null,
            providers,
            adapters,
            gate,
            now: () => Start);
    }

    private static async Task<DelegationExecutionSnapshot> PumpOnceAsync(
        DelegationRuntime runtime, DelegationId id, CancellationToken ct)
    {
        var snapshot = await runtime.GetAsync(id, ct);
        return await runtime.PumpAsync(id, snapshot.Progress.Revision, ct);
    }

    private static async Task<DelegationExecutionSnapshot> PumpToTerminalAsync(
        DelegationRuntime runtime, DelegationId id, CancellationToken ct)
    {
        var snapshot = await runtime.GetAsync(id, ct);
        for (var i = 0; i < 50 && !DelegationLifecycle.IsTerminal(snapshot.Progress.State); i++)
        {
            snapshot = await runtime.PumpAsync(id, snapshot.Progress.Revision, ct);
        }
        return snapshot;
    }

    private sealed class SwitchableGate(ProviderStartAuthorizationStatus status) : IProviderStartAuthorizer
    {
        private readonly List<ProviderStartAuthorizationContext> contexts = [];
        public IReadOnlyList<ProviderStartAuthorizationContext> Contexts => contexts;
        public ProviderStartAuthorizationStatus Status { get; set; } = status;

        public ValueTask<ProviderStartAuthorizationOutcome> AuthorizeAsync(
            ProviderStartAuthorizationContext context, CancellationToken cancellationToken = default)
        {
            contexts.Add(context);
            return ValueTask.FromResult(new ProviderStartAuthorizationOutcome(Status, "test." + Status));
        }
    }

    private sealed class ThrowingGate : IProviderStartAuthorizer
    {
        public ValueTask<ProviderStartAuthorizationOutcome> AuthorizeAsync(
            ProviderStartAuthorizationContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("gate down");
    }

    private sealed class NullGate : IProviderStartAuthorizer
    {
        public ValueTask<ProviderStartAuthorizationOutcome> AuthorizeAsync(
            ProviderStartAuthorizationContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ProviderStartAuthorizationOutcome>(null!);
    }

    private sealed class RecordingProvider : IExternalOperationProvider
    {
        private readonly List<ExternalOperationStartRequest> startRequests = [];
        public IReadOnlyList<ExternalOperationStartRequest> StartRequests => startRequests;

        public ValueTask<ExternalOperationStartReceipt> StartAsync(
            ExternalOperationStartRequest request,
            IExternalOperationHandleCaptureSink handleSink,
            CancellationToken cancellationToken = default)
        {
            startRequests.Add(request);
            var handle = new ExternalOperationHandle(
                request.Correlation.Agent.Provider, "start-handle",
                request.Correlation.Agent.ProtocolVersion,
                new ExternalOperationCorrelation(request.Correlation.DelegationId,
                    request.Correlation.WorkflowRun, request.Correlation.StructuralNode,
                    request.Correlation.NodeGeneration, request.Correlation.ExecutionAttemptId,
                    request.Correlation.Agent,
                    new ExternalTaskReference(request.Correlation.Agent.Provider, "start-task")));
            return ValueTask.FromResult(new ExternalOperationStartReceipt(
                request.Identity, handle, ExternalOperationStartDisposition.Created,
                ExternalOperationState.Running, Start.AddMinutes(1)));
        }

        public ValueTask<ExternalOperationObservation> ObserveAsync(
            ExternalOperationHandle operationHandle, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ExternalOperationObservation(
                operationHandle, 1, ExternalOperationState.Succeeded, Start.AddMinutes(2), resultAvailable: true));

        public ValueTask<ExternalOperationResult> GetResultAsync(
            ExternalOperationHandle operationHandle, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ExternalOperationResult(
                operationHandle, ExternalOperationState.Succeeded, Start.AddMinutes(3), "done", []));

        public ValueTask<ExternalOperationCancellationReceipt> CancelAsync(
            ExternalOperationCancelRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<ExternalOperationResumeReceipt> ResumeAsync(
            ExternalOperationResumeRequest request, IExternalOperationHandleCaptureSink handleSink,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
