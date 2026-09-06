#pragma warning disable xUnit1051

using FluentAssertions;

namespace Penghou.Qingniao.Tests;

public sealed class M25CoordinatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Waiting_publishes_checkpoint_and_hint_and_ordinary_pump_is_a_noop()
    {
        var provider = new WaitingProvider();
        var (coordinator, _) = CreateCoordinator(provider);
        var accepted = await coordinator.AcceptAsync(new DelegationCallerScope("caller"), Request("waiting"));
        var queued = await coordinator.GetAsync(accepted.DelegationId);
        var running = await coordinator.PumpAsync(accepted.DelegationId, queued.Progress.Revision);
        var waiting = await coordinator.PumpAsync(accepted.DelegationId, running.Progress.Revision);

        waiting.Progress.State.Should().Be(DelegationState.WaitingForSupervisor);
        waiting.Progress.Checkpoint.Should().NotBeNull();
        waiting.Progress.Checkpoint!.ExpectedObservableRevision.Should().Be(waiting.Progress.Revision);
        var hint = await coordinator.GetWakeHintAsync(accepted.DelegationId);
        hint.Should().NotBeNull();
        hint!.CheckpointId.Should().Be(waiting.Progress.Checkpoint.CheckpointId);
        hint.AfterRevision.Should().Be(waiting.Progress.Revision);

        var directResume = () => coordinator.ResumeAsync(accepted.DelegationId, waiting.Progress.Revision, "direct-resume").AsTask();
        await directResume.Should().ThrowAsync<InvalidOperationException>();
        provider.ResumeCalls.Should().Be(0);

        var replay = await coordinator.PumpAsync(accepted.DelegationId, waiting.Progress.Revision);
        replay.Progress.Should().BeSameAs(waiting.Progress);
        provider.ObserveCalls.Should().Be(1);
    }

    [Fact]
    public async Task Context_is_exactly_fenced_and_approve_activates_resume_once()
    {
        var provider = new WaitingProvider();
        var context = new RecordingContextProvider();
        var registry = new InMemorySupervisorInterventionAcceptanceRegistry();
        var (coordinator, _) = CreateCoordinator(provider, context, registry);
        var accepted = await coordinator.AcceptAsync(new DelegationCallerScope("caller"), Request("approve"));
        var queued = await coordinator.GetAsync(accepted.DelegationId);
        var running = await coordinator.PumpAsync(accepted.DelegationId, queued.Progress.Revision);
        var waiting = await coordinator.PumpAsync(accepted.DelegationId, running.Progress.Revision);
        var supervisor = new SupervisorIdentity("host", "operator");
        await coordinator.ActivateCheckpointAsync(accepted.DelegationId, supervisor);

        var request = new SupervisorContextRequest(
            accepted.DelegationId,
            waiting.Progress.Checkpoint!.CheckpointId,
            waiting.Progress.Revision,
            SupervisorContextFacet.Status,
            new SupervisorContextLimits(4, 1_024));
        var package = await coordinator.GetContextAsync(supervisor, request);
        package.ValidateAgainst(request, waiting.Progress);
        context.Calls.Should().Be(1);

        var intervention = new SupervisorIntervention(
            accepted.DelegationId,
            waiting.Progress.Checkpoint.CheckpointId,
            "approve-1",
            waiting.Progress.Revision,
            new SupervisorAction.Approve("continue"));
        var resumed = await coordinator.ApplyInterventionAsync(supervisor, intervention);
        var replay = await coordinator.ApplyInterventionAsync(supervisor, intervention);
        resumed.Progress.State.Should().Be(DelegationState.Running);
        replay.Progress.Should().BeSameAs(resumed.Progress);
        provider.ResumeCalls.Should().Be(1);
        resumed.Progress.Checkpoint.Should().BeNull();
        resumed.Progress.DelegationId.Should().Be(accepted.DelegationId);
    }

    [Fact]
    public async Task Unsupported_action_is_rejected_before_registry_acceptance()
    {
        var provider = new WaitingProvider();
        var registry = new CountingRegistry();
        var (coordinator, _) = CreateCoordinator(provider, registry: registry);
        var accepted = await coordinator.AcceptAsync(new DelegationCallerScope("caller"), Request("unsupported"));
        var queued = await coordinator.GetAsync(accepted.DelegationId);
        var running = await coordinator.PumpAsync(accepted.DelegationId, queued.Progress.Revision);
        var waiting = await coordinator.PumpAsync(accepted.DelegationId, running.Progress.Revision);
        var intervention = new SupervisorIntervention(
            accepted.DelegationId,
            waiting.Progress.Checkpoint!.CheckpointId,
            "reject-unsupported",
            waiting.Progress.Revision,
            new SupervisorAction.Reject("no"));

        var act = () => coordinator.ApplyInterventionAsync(new SupervisorIdentity("host", "operator"), intervention).AsTask();
        await act.Should().ThrowAsync<NotSupportedException>();
        registry.AcceptCalls.Should().Be(0);
    }

    [Fact]
    public async Task Stale_or_wrong_checkpoint_and_terminal_interventions_never_reach_registry()
    {
        var provider = new WaitingProvider();
        var registry = new CountingRegistry();
        var (coordinator, _) = CreateCoordinator(provider, registry: registry);
        var accepted = await coordinator.AcceptAsync(new DelegationCallerScope("caller"), Request("fences"));
        var queued = await coordinator.GetAsync(accepted.DelegationId);
        var running = await coordinator.PumpAsync(accepted.DelegationId, queued.Progress.Revision);
        var waiting = await coordinator.PumpAsync(accepted.DelegationId, running.Progress.Revision);
        var supervisor = new SupervisorIdentity("host", "operator");

        var wrongCheckpoint = new SupervisorIntervention(accepted.DelegationId, new SupervisorCheckpointId(Guid.NewGuid()), "wrong", waiting.Progress.Revision, new SupervisorAction.Approve());
        var stale = new SupervisorIntervention(accepted.DelegationId, waiting.Progress.Checkpoint!.CheckpointId, "stale", waiting.Progress.Revision - 1, new SupervisorAction.Approve());
        var wrong = () => coordinator.ApplyInterventionAsync(supervisor, wrongCheckpoint).AsTask();
        var staleAct = () => coordinator.ApplyInterventionAsync(supervisor, stale).AsTask();
        await wrong.Should().ThrowAsync<SupervisorInterventionRejectedException>();
        await staleAct.Should().ThrowAsync<SupervisorInterventionRejectedException>();
        registry.AcceptCalls.Should().Be(0);

        var cancelled = await coordinator.CancelAsync(accepted.DelegationId, waiting.Progress.Revision, "cancel", "stop");
        var terminal = new SupervisorIntervention(accepted.DelegationId, waiting.Progress.Checkpoint!.CheckpointId, "terminal", cancelled.Progress.Revision, new SupervisorAction.Approve());
        var terminalAct = () => coordinator.ApplyInterventionAsync(supervisor, terminal).AsTask();
        await terminalAct.Should().ThrowAsync<SupervisorInterventionRejectedException>();
        registry.AcceptCalls.Should().Be(0);
    }

    [Fact]
    public async Task Accepted_intervention_replay_recovers_after_cancellation_after_acceptance()
    {
        var provider = new WaitingProvider();
        using var cancelled = new CancellationTokenSource();
        var registry = new CancelAfterAcceptRegistry(cancelled);
        var (coordinator, _) = CreateCoordinator(provider, registry: registry);
        var accepted = await coordinator.AcceptAsync(new DelegationCallerScope("caller"), Request("accept-cancel"));
        var queued = await coordinator.GetAsync(accepted.DelegationId);
        var running = await coordinator.PumpAsync(accepted.DelegationId, queued.Progress.Revision);
        var waiting = await coordinator.PumpAsync(accepted.DelegationId, running.Progress.Revision);
        var supervisor = new SupervisorIdentity("host", "operator");
        await coordinator.ActivateCheckpointAsync(accepted.DelegationId, supervisor);
        var intervention = new SupervisorIntervention(accepted.DelegationId, waiting.Progress.Checkpoint!.CheckpointId, "accept-cancel-1", waiting.Progress.Revision, new SupervisorAction.Approve());

        var first = () => coordinator.ApplyInterventionAsync(supervisor, intervention, cancelled.Token).AsTask();
        await first.Should().ThrowAsync<OperationCanceledException>();
        var replay = await coordinator.ApplyInterventionAsync(supervisor, intervention);
        replay.Progress.State.Should().Be(DelegationState.Running);
        provider.ResumeCalls.Should().Be(1);
    }

    [Fact]
    public async Task Second_waiting_episode_gets_a_new_checkpoint_and_hint_expires()
    {
        var provider = new WaitingProvider { WaitAgain = true };
        var now = Start;
        var (coordinator, _) = CreateCoordinator(provider, now: () => now);
        var accepted = await coordinator.AcceptAsync(new DelegationCallerScope("caller"), Request("episodes"));
        var queued = await coordinator.GetAsync(accepted.DelegationId);
        var running = await coordinator.PumpAsync(accepted.DelegationId, queued.Progress.Revision);
        var firstWaiting = await coordinator.PumpAsync(accepted.DelegationId, running.Progress.Revision);
        var firstHint = await coordinator.GetWakeHintAsync(accepted.DelegationId);
        var supervisor = new SupervisorIdentity("host", "operator");
        await coordinator.ActivateCheckpointAsync(accepted.DelegationId, supervisor);
        var intervention = new SupervisorIntervention(accepted.DelegationId, firstWaiting.Progress.Checkpoint!.CheckpointId, "episode-approve", firstWaiting.Progress.Revision, new SupervisorAction.Approve());
        var resumed = await coordinator.ApplyInterventionAsync(supervisor, intervention);
        (await coordinator.GetWakeHintAsync(accepted.DelegationId)).Should().BeNull();

        var secondWaiting = await coordinator.PumpAsync(accepted.DelegationId, resumed.Progress.Revision);
        secondWaiting.Progress.State.Should().Be(DelegationState.WaitingForSupervisor);
        secondWaiting.Progress.Checkpoint!.CheckpointId.Should().NotBe(firstWaiting.Progress.Checkpoint.CheckpointId);
        (await coordinator.GetWakeHintAsync(accepted.DelegationId))!.CheckpointId.Should().Be(secondWaiting.Progress.Checkpoint.CheckpointId);

        var firstReplayOnSecondEpisode = () => coordinator.ApplyInterventionAsync(supervisor, intervention).AsTask();
        await firstReplayOnSecondEpisode.Should().ThrowAsync<SupervisorInterventionRejectedException>();
        await coordinator.ActivateCheckpointAsync(accepted.DelegationId, supervisor);
        var secondIntervention = new SupervisorIntervention(accepted.DelegationId, secondWaiting.Progress.Checkpoint.CheckpointId, "episode-approve-2", secondWaiting.Progress.Revision, new SupervisorAction.Approve());
        var resumedAgain = await coordinator.ApplyInterventionAsync(supervisor, secondIntervention);
        var secondReplay = await coordinator.ApplyInterventionAsync(supervisor, secondIntervention);
        resumedAgain.Progress.State.Should().Be(DelegationState.Running);
        secondReplay.Progress.Should().BeSameAs(resumedAgain.Progress);
        provider.ResumeCalls.Should().Be(2);
        now = secondWaiting.Progress.UpdatedAt.AddMinutes(31);
        (await coordinator.GetWakeHintAsync(accepted.DelegationId)).Should().BeNull();
        firstHint!.ExpiresAt.Should().BeAfter(firstWaiting.Progress.UpdatedAt);
    }

    [Fact]
    public async Task Context_callback_runs_outside_gate_and_stale_fence_is_rejected_on_return()
    {
        var provider = new WaitingProvider();
        var context = new AdvancingContextProvider();
        var (coordinator, _) = CreateCoordinator(provider, context);
        var accepted = await coordinator.AcceptAsync(new DelegationCallerScope("caller"), Request("context-race"));
        var queued = await coordinator.GetAsync(accepted.DelegationId);
        var running = await coordinator.PumpAsync(accepted.DelegationId, queued.Progress.Revision);
        var waiting = await coordinator.PumpAsync(accepted.DelegationId, running.Progress.Revision);
        var supervisor = new SupervisorIdentity("host", "operator");
        var request = new SupervisorContextRequest(accepted.DelegationId, waiting.Progress.Checkpoint!.CheckpointId, waiting.Progress.Revision, SupervisorContextFacet.Status, new SupervisorContextLimits(4, 1_024));
        context.Advance = () => coordinator.CancelAsync(accepted.DelegationId, waiting.Progress.Revision, "context-race-cancel", "advance").AsTask();

        var act = () => coordinator.GetContextAsync(supervisor, request).AsTask();
        await act.Should().ThrowAsync<ArgumentException>();
        context.Calls.Should().Be(1);
    }

    private static (InMemoryDelegationCoordinator Coordinator, InMemoryExternalOperationHandleCaptureRegistry Captures) CreateCoordinator(
        WaitingProvider provider,
        ISupervisorContextProvider? context = null,
        ISupervisorInterventionAcceptanceRegistry? registry = null,
        Func<DateTimeOffset>? now = null)
    {
        var descriptor = new ProviderDescriptor("m25-provider", [new CapabilityDescriptor("agent.execute", 1)]);
        var providers = new InMemoryProviderRegistry();
        providers.Register(descriptor);
        var adapters = new InMemoryExternalOperationProviderCatalog();
        adapters.Register(descriptor, provider);
        var captures = new InMemoryExternalOperationHandleCaptureRegistry();
        return (new InMemoryDelegationCoordinator(
            new InMemoryDelegationAcceptanceRegistry(),
            new InMemoryWorkflowPlanResolver(),
            providers,
            adapters,
            new SimingExternalOperationSemanticFingerprintVerifier(),
            captures,
            now: now ?? (() => Start),
            contextProvider: context,
            interventionRegistry: registry), captures);
    }

    private static DelegationRequest Request(string key) => new(
        key,
        "Implement the objective",
        new WorkspaceReference("local", "workspace", "revision"),
        ["The result is correct"],
        [],
        new DelegationBudget(8, 2));

    private sealed class WaitingProvider : IExternalOperationProvider
    {
        private ExternalOperationHandle? handle;
        public int ObserveCalls { get; private set; }
        public int ResumeCalls { get; private set; }
        public bool WaitAgain { get; init; }

        public async ValueTask<ExternalOperationStartReceipt> StartAsync(ExternalOperationStartRequest request, IExternalOperationHandleCaptureSink handleSink, CancellationToken cancellationToken = default)
        {
            handle = Handle(request, "m25-handle");
            await handleSink.CaptureAsync(new ExternalOperationHandleCapture(handle, Start.AddMinutes(1)), cancellationToken);
            return new ExternalOperationStartReceipt(request.Identity, handle, ExternalOperationStartDisposition.Created, ExternalOperationState.Running, Start.AddMinutes(1));
        }

        public ValueTask<ExternalOperationObservation> ObserveAsync(ExternalOperationHandle operationHandle, CancellationToken cancellationToken = default)
        {
            ObserveCalls++;
            var state = ResumeCalls == 0 || (WaitAgain && ResumeCalls == 1)
                ? ExternalOperationState.Waiting
                : ExternalOperationState.Running;
            return ValueTask.FromResult(new ExternalOperationObservation(operationHandle, ObserveCalls, state, Start.AddMinutes(2 + ObserveCalls), providerStatus: "needs approval"));
        }

        public ValueTask<ExternalOperationResult> GetResultAsync(ExternalOperationHandle operationHandle, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ExternalOperationResult(operationHandle, ExternalOperationState.Succeeded, Start.AddMinutes(10), "done", []));

        public ValueTask<ExternalOperationCancellationReceipt> CancelAsync(ExternalOperationCancelRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ExternalOperationCancellationReceipt(request.Handle, request.CancellationKey, ExternalOperationCancellationDisposition.Requested, ExternalOperationState.CancellationRequested, Start.AddMinutes(3)));

        public async ValueTask<ExternalOperationResumeReceipt> ResumeAsync(ExternalOperationResumeRequest request, IExternalOperationHandleCaptureSink handleSink, CancellationToken cancellationToken = default)
        {
            ResumeCalls++;
            await handleSink.CaptureAsync(new ExternalOperationHandleCapture(request.Handle, Start.AddMinutes(4)), cancellationToken);
            return new ExternalOperationResumeReceipt(request.Handle, request.ResumeKey, request.Handle, ExternalOperationStartDisposition.Existing, ExternalOperationState.Running, Start.AddMinutes(4));
        }

        private static ExternalOperationHandle Handle(ExternalOperationStartRequest request, string value) => new(
            request.Correlation.Agent.Provider, value, request.Correlation.Agent.ProtocolVersion,
            new ExternalOperationCorrelation(request.Correlation.DelegationId, request.Correlation.WorkflowRun,
                request.Correlation.StructuralNode, request.Correlation.NodeGeneration,
                request.Correlation.ExecutionAttemptId, request.Correlation.Agent,
                new ExternalTaskReference(request.Correlation.Agent.Provider, value)));
    }

    private sealed class RecordingContextProvider : ISupervisorContextProvider
    {
        public int Calls { get; private set; }
        public ValueTask<SupervisorContextPackage> GetAsync(SupervisorIdentity supervisor, SupervisorContextRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(new SupervisorContextPackage(
                request.DelegationId, request.CheckpointId, request.ExpectedRevision,
                SupervisorContextFacet.Status, request.Limits,
                [new SupervisorContextItem(SupervisorContextFacet.Status, "state", "waiting")], [], [], [],
                [new ContextFacetOutcome(SupervisorContextFacet.Status, ContextFacetAvailability.Included, 1)]));
        }
    }

    private sealed class AdvancingContextProvider : ISupervisorContextProvider
    {
        public int Calls { get; private set; }
        public Func<Task>? Advance { get; set; }

        public async ValueTask<SupervisorContextPackage> GetAsync(SupervisorIdentity supervisor, SupervisorContextRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Advance is not null)
            {
                await Advance();
            }

            return new SupervisorContextPackage(
                request.DelegationId, request.CheckpointId, request.ExpectedRevision,
                SupervisorContextFacet.Status, request.Limits,
                [new SupervisorContextItem(SupervisorContextFacet.Status, "state", "waiting")], [], [], [],
                [new ContextFacetOutcome(SupervisorContextFacet.Status, ContextFacetAvailability.Included, 1)]);
        }
    }

    private sealed class CountingRegistry : ISupervisorInterventionAcceptanceRegistry
    {
        public int AcceptCalls { get; private set; }
        public ValueTask ActivateAsync(SupervisorIdentity supervisor, DelegationProgress waitingProgress, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask<SupervisorInterventionAcceptance> AcceptAsync(SupervisorIdentity supervisor, SupervisorIntervention intervention, CancellationToken cancellationToken = default)
        {
            AcceptCalls++;
            throw new InvalidOperationException("should not be called");
        }
    }

    private sealed class CancelAfterAcceptRegistry(CancellationTokenSource source) : ISupervisorInterventionAcceptanceRegistry
    {
        private readonly InMemorySupervisorInterventionAcceptanceRegistry inner = new();
        public ValueTask ActivateAsync(SupervisorIdentity supervisor, DelegationProgress waitingProgress, CancellationToken cancellationToken = default) =>
            inner.ActivateAsync(supervisor, waitingProgress, cancellationToken);

        public async ValueTask<SupervisorInterventionAcceptance> AcceptAsync(SupervisorIdentity supervisor, SupervisorIntervention intervention, CancellationToken cancellationToken = default)
        {
            var acceptance = await inner.AcceptAsync(supervisor, intervention, CancellationToken.None);
            source.Cancel();
            return acceptance;
        }
    }
}
