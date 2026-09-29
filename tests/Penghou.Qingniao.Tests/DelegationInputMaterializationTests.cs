using FluentAssertions;

namespace Penghou.Qingniao.Tests;

/// <summary>
/// QH-04 regressions: admitted execution inputs reach the provider through
/// the fingerprinted start identity, and partial recovery fails closed.
/// </summary>
public sealed class DelegationInputMaterializationTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Different_objectives_produce_distinct_provider_inputs()
    {
        var materializer = new StubMaterializer { Rule = ObjectiveInputs };
        var (coordinator, provider) = CreateCoordinator(materializer);
        var ct = TestContext.Current.CancellationToken;

        var first = await coordinator.AcceptAsync(
            Caller(), CreateRequest("inputs-alpha", objective: "alpha objective", workspaceId: "ws-alpha"), ct);
        await PumpOnceAsync(coordinator, first.DelegationId, ct);
        var second = await coordinator.AcceptAsync(
            Caller(), CreateRequest("inputs-beta", objective: "beta objective", workspaceId: "ws-beta"), ct);
        await PumpOnceAsync(coordinator, second.DelegationId, ct);

        provider.SeenStarts.Should().HaveCount(2);
        var alpha = provider.SeenStarts[0];
        var beta = provider.SeenStarts[1];
        alpha.InputArtifacts.Should().ContainSingle().Which.ArtifactId.Should().Be("input-alpha");
        beta.InputArtifacts.Should().ContainSingle().Which.ArtifactId.Should().Be("input-beta");
        alpha.Identity.SemanticFingerprint.Should().NotBe(beta.Identity.SemanticFingerprint);
        alpha.Budget!.MaximumTokens.Should().Be(1_000);
        beta.Budget!.MaximumTokens.Should().Be(2_000);
        alpha.Deadline.Should().Be(Start.AddHours(1));
        beta.Deadline.Should().Be(Start.AddHours(2));
        alpha.Correlation.Agent.ProtocolVersion.Should().Be("host.protocol.v1");
        alpha.Correlation.Agent.Provider.Should().Be("fake-provider");

        // The fingerprint genuinely covers the received inputs.
        var verifier = new LocalSemanticFingerprintVerifier();
        alpha.VerifySemanticFingerprint(verifier);
        beta.VerifySemanticFingerprint(verifier);
        verifier.Compute(alpha.SemanticInput).Should().Be(alpha.Identity.SemanticFingerprint);
    }

    [Fact]
    public void Altered_input_changes_the_semantic_fingerprint()
    {
        var verifier = new LocalSemanticFingerprintVerifier();
        var agent = new ExternalAgentReference("fake-provider", "fake-agent", "host.protocol.v1");
        var id = new DelegationId(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var envelope = new ExternalOperationSemanticInputEnvelope(
            id, agent, "agent.execute", [], null, null);
        var altered = new ExternalOperationSemanticInputEnvelope(
            id, agent, "agent.execute", [TestArtifact(id, "input-x")], null, Start.AddHours(1));

        verifier.Compute(envelope).Should().NotBe(verifier.Compute(altered));
    }

    [Fact]
    public async Task Inconsistent_identity_fails_verification_before_launch()
    {
        var (coordinator, provider) = CreateCoordinator(
            new StubMaterializer { Rule = ObjectiveInputs },
            fingerprintVerifier: new RejectingVerifier());
        var ct = TestContext.Current.CancellationToken;

        var act = () => coordinator.AcceptAsync(Caller(), CreateRequest("bad-identity"), ct).AsTask();

        await act.Should().ThrowAsync<InvalidOperationException>();
        provider.StartCalls.Should().Be(0);
    }

    [Fact]
    public async Task Interrupted_initial_acceptance_does_not_duplicate_work()
    {
        var materializer = new StubMaterializer { Rule = ObjectiveInputs, FailFirstCalls = 1 };
        var (coordinator, provider) = CreateCoordinator(materializer);
        var ct = TestContext.Current.CancellationToken;
        var request = CreateRequest("interrupted-init");

        var interrupted = () => coordinator.AcceptAsync(Caller(), request, ct).AsTask();
        await interrupted.Should().ThrowAsync<InvalidOperationException>();

        var acceptance = await coordinator.AcceptAsync(Caller(), request, ct);
        acceptance.IsNew.Should().BeFalse();
        await PumpToTerminalAsync(coordinator, acceptance.DelegationId, ct);

        materializer.Calls.Should().Be(2);
        provider.StartCalls.Should().Be(1);
        var terminal = await coordinator.GetAsync(acceptance.DelegationId, ct);
        terminal.Progress.State.Should().Be(DelegationState.Completed);
    }

    [Fact]
    public async Task Retained_acceptance_with_execution_record_cannot_silently_relaunch()
    {
        var acceptanceRegistry = new InMemoryDelegationAcceptanceRegistry();
        var executionStore = new InMemoryDelegationExecutionStore();
        var first = CreateCoordinator(acceptanceRegistry: acceptanceRegistry, executionStore: executionStore);
        var ct = TestContext.Current.CancellationToken;
        var request = CreateRequest("retained-running");

        var acceptance = await first.Coordinator.AcceptAsync(Caller(), request, ct);
        await PumpOnceAsync(first.Coordinator, acceptance.DelegationId, ct);
        first.Provider.StartCalls.Should().Be(1);

        var second = CreateCoordinator(
            acceptanceRegistry: acceptanceRegistry,
            executionStore: executionStore);

        var relaunch = () => second.Coordinator.AcceptAsync(Caller(), request, ct).AsTask();
        await relaunch.Should().ThrowAsync<DelegationCoordinatorStateUnavailableException>();
        second.Provider.StartCalls.Should().Be(0);
    }

    [Fact]
    public async Task Retained_acceptance_with_queued_record_cannot_silently_relaunch()
    {
        var acceptanceRegistry = new InMemoryDelegationAcceptanceRegistry();
        var executionStore = new InMemoryDelegationExecutionStore();
        var ct = TestContext.Current.CancellationToken;
        var request = CreateRequest("retained-queued");

        var preaccepted = await acceptanceRegistry.AcceptAsync(Caller(), request, ct);
        await executionStore.CreateAsync(preaccepted.DelegationId, Start, ct);

        var coordinator = CreateCoordinator(
            acceptanceRegistry: acceptanceRegistry,
            executionStore: executionStore).Coordinator;

        var relaunch = () => coordinator.AcceptAsync(Caller(), request, ct).AsTask();
        await relaunch.Should().ThrowAsync<DelegationCoordinatorStateUnavailableException>();
    }

    [Fact]
    public async Task Trusted_protocol_mismatch_fails_before_launch()
    {
        var materializer = new StubMaterializer
        {
            Rule = (request, id) => new MaterializedDelegationInputs(
                [],
                null,
                null,
                new ExternalAgentReference("other-provider", "other-agent", "other.protocol.v9")),
        };
        var (coordinator, provider) = CreateCoordinator(materializer);
        var ct = TestContext.Current.CancellationToken;

        var act = () => coordinator.AcceptAsync(Caller(), CreateRequest("protocol-mismatch"), ct).AsTask();

        await act.Should().ThrowAsync<InvalidOperationException>();
        provider.StartCalls.Should().Be(0);
    }

    [Fact]
    public async Task Default_unmaterialized_start_preserves_legacy_identity()
    {
        var (coordinator, provider) = CreateCoordinator();
        var ct = TestContext.Current.CancellationToken;

        var acceptance = await coordinator.AcceptAsync(Caller(), CreateRequest("legacy-start"), ct);
        await PumpOnceAsync(coordinator, acceptance.DelegationId, ct);

        var start = provider.SeenStarts.Should().ContainSingle().Subject;
        start.InputArtifacts.Should().BeEmpty();
        start.Budget.Should().BeNull();
        start.Deadline.Should().BeNull();
        start.Correlation.Agent.Provider.Should().Be("fake-provider");
        start.Correlation.Agent.ProtocolVersion.Should().Be("qingniao-inmemory-v1");
        start.VerifySemanticFingerprint(new LocalSemanticFingerprintVerifier());
    }

    [Fact]
    public void Materialized_inputs_reject_duplicates_and_null_agent()
    {
        var id = new DelegationId(Guid.Parse("00000000-0000-0000-0000-000000000002"));
        var agent = new ExternalAgentReference("fake-provider", "fake-agent", "host.protocol.v1");
        var duplicate = () => new MaterializedDelegationInputs(
            [TestArtifact(id, "input-x"), TestArtifact(id, "input-x")], null, null, agent);
        duplicate.Should().Throw<ArgumentException>();

        var nullAgent = () => new MaterializedDelegationInputs([], null, null, null!);
        nullAgent.Should().Throw<ArgumentNullException>();
    }

    private static MaterializedDelegationInputs ObjectiveInputs(DelegationRequest request, DelegationId id)
    {
        var slug = request.Objective.Contains("alpha", StringComparison.Ordinal) ? "alpha" : "beta";
        var tokens = slug == "alpha" ? 1_000 : 2_000;
        var deadline = slug == "alpha" ? Start.AddHours(1) : Start.AddHours(2);
        return new MaterializedDelegationInputs(
            [TestArtifact(id, "input-" + slug)],
            new ExternalOperationBudgetHint(tokens, TimeSpan.FromHours(1)),
            deadline,
            new ExternalAgentReference("fake-provider", "fake-agent", "host.protocol.v1"));
    }

    private static DelegationArtifactReference TestArtifact(DelegationId id, string artifactId) =>
        new(
            id,
            new StructuralNodeReference("delegation"),
            new NodeGenerationId(id.Value),
            "fake-provider",
            "host-repository",
            artifactId,
            "text/plain",
            1,
            "memory://" + artifactId,
            ArtifactContentIdentity.Sha256Bytes("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"));

    private static async Task PumpOnceAsync(
        InMemoryDelegationCoordinator coordinator,
        DelegationId delegationId,
        CancellationToken cancellationToken)
    {
        var queued = await coordinator.GetAsync(delegationId, cancellationToken);
        await coordinator.PumpAsync(delegationId, queued.Progress.Revision, cancellationToken);
    }

    private static async Task PumpToTerminalAsync(
        InMemoryDelegationCoordinator coordinator,
        DelegationId delegationId,
        CancellationToken cancellationToken)
    {
        var snapshot = await coordinator.GetAsync(delegationId, cancellationToken);
        for (var index = 0; index < 20 && !DelegationLifecycle.IsTerminal(snapshot.Progress.State); index++)
        {
            snapshot = await coordinator.PumpAsync(delegationId, snapshot.Progress.Revision, cancellationToken);
        }

        DelegationLifecycle.IsTerminal(snapshot.Progress.State).Should().BeTrue();
    }

    private static (InMemoryDelegationCoordinator Coordinator, CapturingProvider Provider) CreateCoordinator(
        IDelegationInputMaterializer? materializer = null,
        IDelegationAcceptanceRegistry? acceptanceRegistry = null,
        InMemoryDelegationExecutionStore? executionStore = null,
        IExternalOperationSemanticFingerprintVerifier? fingerprintVerifier = null)
    {
        var provider = new CapturingProvider();
        var descriptor = new ProviderDescriptor(
            "fake-provider",
            [new CapabilityDescriptor("agent.execute", 1)]);
        var providers = new InMemoryProviderRegistry();
        providers.Register(descriptor);
        var adapters = new InMemoryExternalOperationProviderCatalog();
        adapters.Register(descriptor, provider);
        var coordinator = new InMemoryDelegationCoordinator(
            acceptanceRegistry ?? new InMemoryDelegationAcceptanceRegistry(),
            null,
            providers,
            adapters,
            fingerprintVerifier ?? new LocalSemanticFingerprintVerifier(),
            new InMemoryExternalOperationHandleCaptureRegistry(),
            executionStore,
            now: () => Start,
            inputMaterializer: materializer);
        return (coordinator, provider);
    }

    private static DelegationCallerScope Caller() => new("caller-1");

    private static DelegationRequest CreateRequest(
        string requestKey,
        string objective = "Implement the objective",
        string workspaceId = "workspace") => new(
        requestKey,
        objective,
        "fake-provider",
        new WorkspaceReference("local", workspaceId, "revision"),
        ["The result is correct"],
        [],
        new DelegationBudget(MaximumWorkerCalls: 4, MaximumRetries: 1));

    private sealed class StubMaterializer : IDelegationInputMaterializer
    {
        public int Calls;

        public int FailFirstCalls;

        public Func<DelegationRequest, DelegationId, MaterializedDelegationInputs>? Rule;

        public MaterializedDelegationInputs Materialize(DelegationRequest request, DelegationId delegationId)
        {
            Calls++;
            if (Calls <= FailFirstCalls)
            {
                throw new InvalidOperationException("The materializer failed before initialization completed.");
            }

            return Rule is null
                ? throw new InvalidOperationException("No materialization rule configured.")
                : Rule(request, delegationId);
        }
    }

    private sealed class RejectingVerifier : IExternalOperationSemanticFingerprintVerifier
    {
        public string Compute(ExternalOperationSemanticInputEnvelope semanticInput) => new('0', 64);

        public bool Matches(ExternalOperationStartIdentity identity, ExternalOperationSemanticInputEnvelope semanticInput) => false;
    }

    private sealed class CapturingProvider : IExternalOperationProvider
    {
        private static readonly DateTimeOffset AcceptedAt = Start.AddMinutes(1);

        public int StartCalls;

        public int ObserveCalls;

        public List<ExternalOperationStartRequest> SeenStarts { get; } = [];

        public async ValueTask<ExternalOperationStartReceipt> StartAsync(
            ExternalOperationStartRequest request,
            IExternalOperationHandleCaptureSink handleSink,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCalls++;
            SeenStarts.Add(request);
            var correlation = new ExternalOperationCorrelation(
                request.Correlation.DelegationId,
                request.Correlation.WorkflowRun,
                request.Correlation.StructuralNode,
                request.Correlation.NodeGeneration,
                request.Correlation.ExecutionAttemptId,
                request.Correlation.Agent,
                new ExternalTaskReference(request.Correlation.Agent.Provider, "task-1"));
            var handle = new ExternalOperationHandle(
                request.Correlation.Agent.Provider,
                "handle-1",
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

        public ValueTask<ExternalOperationObservation> ObserveAsync(
            ExternalOperationHandle operationHandle,
            CancellationToken cancellationToken = default)
        {
            ObserveCalls++;
            var state = ObserveCalls <= 1 ? ExternalOperationState.Running : ExternalOperationState.Succeeded;
            return ValueTask.FromResult(new ExternalOperationObservation(
                operationHandle,
                ObserveCalls,
                state,
                AcceptedAt.AddMinutes(ObserveCalls),
                resultAvailable: state == ExternalOperationState.Succeeded));
        }

        public ValueTask<ExternalOperationResult> GetResultAsync(
            ExternalOperationHandle operationHandle,
            CancellationToken cancellationToken = default)
        {
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
            return ValueTask.FromResult(new ExternalOperationResult(
                operationHandle,
                ExternalOperationState.Succeeded,
                AcceptedAt.AddMinutes(3),
                "completed",
                [artifact]));
        }

        public ValueTask<ExternalOperationCancellationReceipt> CancelAsync(
            ExternalOperationCancelRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<ExternalOperationResumeReceipt> ResumeAsync(
            ExternalOperationResumeRequest request,
            IExternalOperationHandleCaptureSink handleSink,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
