using FluentAssertions;

namespace Penghou.Qingniao.Tests;

/// <summary>
/// Proves the host-owned pre-execution authority preflight seam through the
/// public <see cref="DelegationRuntime"/> surface: the preflight runs after the
/// execution identity exists and before any startable record is committed, the
/// permitted attachment is persisted with the generation and delivered on the
/// start request, and Deny/Unavailable both fail closed without starting.
/// </summary>
public sealed class DelegationAuthorityPreflightTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NotBefore = Start;
    private static readonly DateTimeOffset ExpiresAt = Start.AddHours(1);
    private static readonly DelegationExecutionAttachment Attachment =
        new("penghou.hufu.authority-context", "child-grant-123");

    [Fact]
    public async Task Permit_persists_attachment_and_delivers_it_to_the_adapter()
    {
        var provider = new RecordingProvider();
        var preflight = new RecordingPreflight(DelegationAuthorityPreflightDecision.Permit(Attachment));
        var runtime = CreateRuntime(provider, preflight);
        var ct = TestContext.Current.CancellationToken;

        var handle = await runtime.DelegateAsync(new DelegationCallerScope("caller"), CreateRequest("permit", derive: true), ct);

        var queued = await runtime.GetAsync(handle.DelegationId, ct);
        queued.ExecutionAttachment.Should().Be(Attachment);
        preflight.Contexts.Should().ContainSingle()
            .Which.ParentGrantId.Should().Be("grant-parent");
        preflight.Contexts[0].RequestedAuthority.Actions.Should().Equal("ReadFile");
        preflight.Contexts[0].Supervisor.Should().BeNull();

        var terminal = await PumpToTerminalAsync(runtime, handle.DelegationId, ct);

        terminal.Progress.State.Should().Be(DelegationState.Completed);
        terminal.ExecutionAttachment.Should().Be(Attachment);
        provider.StartRequests.Should().ContainSingle()
            .Which.ExecutionAttachment.Should().Be(Attachment);
    }

    [Fact]
    public async Task Deny_fails_closed_without_starting_or_recording_a_startable_snapshot()
    {
        var provider = new RecordingProvider();
        var preflight = new RecordingPreflight(DelegationAuthorityPreflightDecision.Deny("parent revoked"));
        var runtime = CreateRuntime(provider, preflight);
        var ct = TestContext.Current.CancellationToken;

        var act = () => runtime.DelegateAsync(new DelegationCallerScope("caller"), CreateRequest("deny", derive: true), ct);

        var exception = (await act.Should().ThrowAsync<DelegationAuthorityPreflightException>()).Which;
        exception.Status.Should().Be(DelegationAuthorityPreflightStatus.Deny);
        exception.Reason.Should().Be("parent revoked");
        provider.StartRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Unavailable_fails_closed_with_a_distinct_status()
    {
        var provider = new RecordingProvider();
        var preflight = new RecordingPreflight(DelegationAuthorityPreflightDecision.Unavailable("hufu store unreachable"));
        var runtime = CreateRuntime(provider, preflight);
        var ct = TestContext.Current.CancellationToken;

        var act = () => runtime.DelegateAsync(new DelegationCallerScope("caller"), CreateRequest("unavailable", derive: true), ct);

        var exception = (await act.Should().ThrowAsync<DelegationAuthorityPreflightException>()).Which;
        exception.Status.Should().Be(DelegationAuthorityPreflightStatus.Unavailable);
        provider.StartRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Request_without_authority_never_invokes_the_preflight()
    {
        var provider = new RecordingProvider();
        var preflight = new RecordingPreflight(DelegationAuthorityPreflightDecision.Permit(Attachment));
        var runtime = CreateRuntime(provider, preflight);
        var ct = TestContext.Current.CancellationToken;

        var handle = await runtime.DelegateAsync(new DelegationCallerScope("caller"), CreateRequest("plain", derive: false), ct);
        var terminal = await PumpToTerminalAsync(runtime, handle.DelegationId, ct);

        terminal.Progress.State.Should().Be(DelegationState.Completed);
        terminal.ExecutionAttachment.Should().BeNull();
        preflight.Contexts.Should().BeEmpty();
        provider.StartRequests.Should().ContainSingle().Which.ExecutionAttachment.Should().BeNull();
    }

    [Fact]
    public async Task Request_for_authority_without_a_configured_seam_fails_closed_unavailable()
    {
        var provider = new RecordingProvider();
        var runtime = CreateRuntime(provider, authorityPreflight: null);
        var ct = TestContext.Current.CancellationToken;

        var act = () => runtime.DelegateAsync(new DelegationCallerScope("caller"), CreateRequest("no-seam", derive: true), ct);

        var exception = (await act.Should().ThrowAsync<DelegationAuthorityPreflightException>()).Which;
        exception.Status.Should().Be(DelegationAuthorityPreflightStatus.Unavailable);
        provider.StartRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Execution_attachment_is_excluded_from_the_semantic_fingerprint()
    {
        var provider = new RecordingProvider();
        var preflight = new RecordingPreflight(DelegationAuthorityPreflightDecision.Permit(Attachment));
        var runtime = CreateRuntime(provider, preflight);
        var ct = TestContext.Current.CancellationToken;

        var handle = await runtime.DelegateAsync(new DelegationCallerScope("caller"), CreateRequest("fingerprint", derive: true), ct);
        await PumpToTerminalAsync(runtime, handle.DelegationId, ct);

        var captured = provider.StartRequests.Single();
        captured.ExecutionAttachment.Should().Be(Attachment);

        var withoutAttachment = new ExternalOperationStartRequest(
            captured.Identity,
            captured.Correlation,
            captured.Capability,
            captured.InputArtifacts,
            captured.Budget,
            captured.Deadline);

        withoutAttachment.SemanticInput.Should().Be(captured.SemanticInput);
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

    private static DelegationRequest CreateRequest(string requestKey, bool derive)
    {
        var authority = derive
            ? new RequestedAuthority(
                ["ReadFile"],
                new RequestedAuthorityScope("workspace", "src/service", RequestedAuthorityScopeKind.Subtree),
                [],
                NotBefore,
                ExpiresAt)
            : null;

        return new DelegationRequest(
            requestKey,
            "Do the delegated work",
            "authority-provider",
            new WorkspaceReference("local", "workspace", "revision"),
            ["Done"],
            [],
            new DelegationBudget(MaximumWorkerCalls: 8, MaximumRetries: 2),
            parentGrantId: derive ? "grant-parent" : null,
            requestedAuthority: authority);
    }

    private static DelegationRuntime CreateRuntime(
        IExternalOperationProvider provider,
        IDelegationAuthorityPreflight? authorityPreflight)
    {
        var descriptor = new ProviderDescriptor("authority-provider", [new CapabilityDescriptor("agent.execute", 1)]);
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
            authorityPreflight: authorityPreflight);
    }

    private sealed class RecordingPreflight(DelegationAuthorityPreflightDecision decision) : IDelegationAuthorityPreflight
    {
        private readonly List<DelegationAuthorityPreflightContext> contexts = [];

        public IReadOnlyList<DelegationAuthorityPreflightContext> Contexts => contexts;

        public ValueTask<DelegationAuthorityPreflightDecision> PreflightAsync(
            DelegationAuthorityPreflightContext context,
            CancellationToken cancellationToken = default)
        {
            contexts.Add(context);
            return ValueTask.FromResult(decision);
        }
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
            var handle = Handle(request, "authority-handle");
            return ValueTask.FromResult(new ExternalOperationStartReceipt(
                request.Identity, handle, ExternalOperationStartDisposition.Created, ExternalOperationState.Running, Start.AddMinutes(1)));
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
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

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
}
