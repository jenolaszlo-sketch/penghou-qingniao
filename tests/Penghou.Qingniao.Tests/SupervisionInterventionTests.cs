using FluentAssertions;

namespace Penghou.Qingniao.Tests;

public sealed class SupervisionInterventionTests
{
    [Fact]
    public void Typed_actions_have_deterministic_canonical_shape()
    {
        var intervention = CreateIntervention(action: new SupervisorAction.Approve("approve\r\nchange"));
        SupervisorInterventionIdentity.Canonicalize(intervention).Should().Be(
            "{\"action\":{\"kind\":\"approve\",\"rationale\":\"approve\\nchange\"},\"checkpointId\":\"00000000-0000-0000-0000-000000000010\",\"delegationId\":\"00000000-0000-0000-0000-000000000001\",\"expectedRevision\":2}");
        var hash = SupervisorInterventionIdentity.Compute(intervention).Hash;
        hash.Should().Be("17ffcb3c664d1402ee87eaf1b23cd1f56552d0a6ff0b2c80f548752737371a0d");
    }

    [Fact]
    public void Every_typed_action_validates_and_unknown_shapes_are_unrepresentable()
    {
        SupervisorAction[] actions =
        [
            new SupervisorAction.Respond("response"), new SupervisorAction.Approve(), new SupervisorAction.Reject("reason"),
            new SupervisorAction.Retry("retry"), new SupervisorAction.ReexecuteNode(new StructuralNodeReference("node"), "reason"),
            new SupervisorAction.ReexecuteSubgraph(new StructuralNodeReference("root"), "reason"), new SupervisorAction.AddConstraint("constraint"),
            new SupervisorAction.ChangeExecutor(new ExecutorProfileReference("baize", "safe"), "reason"),
            new SupervisorAction.SelectAlternative("alternative", "rationale"), new SupervisorAction.Escalate("reason"), new SupervisorAction.Cancel("reason"),
        ];
        foreach (var action in actions) action.Validate();
        new SupervisorAction.Respond("a\r\nb").Response.Should().Be("a\nb");
        var empty = () => new SupervisorAction.Reject(" ");
        var oversized = () => new SupervisorAction.Respond(new string('x', 16_385));
        empty.Should().Throw<ArgumentException>(); oversized.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Acceptance_is_idempotent_and_receipt_contains_correlation()
    {
        var registry = await CreateRegistry(); var supervisor = CreateSupervisor(); var intervention = CreateIntervention();
        var first = await registry.AcceptAsync(supervisor, intervention, TestContext.Current.CancellationToken); var replay = await registry.AcceptAsync(supervisor, intervention, TestContext.Current.CancellationToken);
        first.IsNew.Should().BeTrue(); replay.IsNew.Should().BeFalse(); replay.ReceiptId.Should().Be(first.ReceiptId);
        first.DelegationId.Should().Be(intervention.DelegationId); first.CheckpointId.Should().Be(intervention.CheckpointId);
        first.ExpectedRevision.Should().Be(intervention.ExpectedRevision); first.Supervisor.Should().Be(supervisor);
    }

    [Fact]
    public async Task Competing_authorized_supervisors_have_one_global_winner()
    {
        var registry = await CreateRegistry(); var second = new SupervisorIdentity("authority-2", "supervisor-2");
        await registry.ActivateAsync(second, CreateWaitingProgress(), TestContext.Current.CancellationToken);
        var attempts = new[] { (CreateSupervisor(), "one"), (second, "two") }.Select(async item =>
        {
            try { return (Success: true, Error: (SupervisorInterventionRejectedException?)null, Receipt: await registry.AcceptAsync(item.Item1, CreateIntervention(item.Item2, new SupervisorAction.Approve(item.Item2)), TestContext.Current.CancellationToken)); }
            catch (SupervisorInterventionRejectedException exception) { return (Success: false, Error: exception, Receipt: (SupervisorInterventionAcceptance?)null); }
        });
        var outcomes = await Task.WhenAll(attempts);
        outcomes.Count(x => x.Success).Should().Be(1); outcomes.Single(x => !x.Success).Error!.Reason.Should().Be(SupervisorInterventionRejectionReason.CheckpointAlreadyDecided);
    }

    [Fact]
    public async Task Duplicate_concurrent_acceptance_reuses_one_receipt()
    {
        var registry = await CreateRegistry(); var intervention = CreateIntervention();
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => registry.AcceptAsync(CreateSupervisor(), intervention, TestContext.Current.CancellationToken).AsTask()));
        outcomes.Select(x => x.ReceiptId).Distinct().Should().ContainSingle(); outcomes.Count(x => x.IsNew).Should().Be(1);
    }

    [Fact]
    public async Task Conflicts_stale_and_unauthorized_requests_are_rejected()
    {
        var registry = await CreateRegistry(); await registry.AcceptAsync(CreateSupervisor(), CreateIntervention(), TestContext.Current.CancellationToken);
        var conflict = () => registry.AcceptAsync(CreateSupervisor(), CreateIntervention("other-key", new SupervisorAction.Reject("reject")), TestContext.Current.CancellationToken).AsTask();
        var stale = () => registry.AcceptAsync(CreateSupervisor(), CreateIntervention("stale", new SupervisorAction.Approve(), 1), TestContext.Current.CancellationToken).AsTask();
        var unauthorized = () => registry.AcceptAsync(new SupervisorIdentity("other", "supervisor"), CreateIntervention("unauthorized"), TestContext.Current.CancellationToken).AsTask();
        (await conflict.Should().ThrowAsync<SupervisorInterventionRejectedException>()).Which.Reason.Should().Be(SupervisorInterventionRejectionReason.CheckpointAlreadyDecided);
        (await stale.Should().ThrowAsync<SupervisorInterventionRejectedException>()).Which.Reason.Should().Be(SupervisorInterventionRejectionReason.CheckpointAlreadyDecided);
        (await unauthorized.Should().ThrowAsync<SupervisorInterventionRejectedException>()).Which.Reason.Should().Be(SupervisorInterventionRejectionReason.CheckpointAlreadyDecided);
    }

    [Fact]
    public async Task Same_supervisor_and_key_with_different_content_is_a_key_conflict()
    {
        var registry = await CreateRegistry();
        await registry.AcceptAsync(CreateSupervisor(), CreateIntervention(), TestContext.Current.CancellationToken);
        var act = () => registry.AcceptAsync(CreateSupervisor(), CreateIntervention(action: new SupervisorAction.Reject("different")), TestContext.Current.CancellationToken).AsTask();
        (await act.Should().ThrowAsync<SupervisorInterventionRejectedException>()).Which.Reason.Should().Be(SupervisorInterventionRejectionReason.ConflictingInterventionKey);
    }

    [Fact]
    public async Task Activation_refresh_is_monotonic_and_same_checkpoint_only()
    {
        var registry = await CreateRegistry(); await registry.ActivateAsync(CreateSupervisor(), CreateWaitingProgress(3), TestContext.Current.CancellationToken);
        var regression = () => registry.ActivateAsync(CreateSupervisor(), CreateWaitingProgress(2), TestContext.Current.CancellationToken).AsTask();
        (await regression.Should().ThrowAsync<SupervisorInterventionRejectedException>()).Which.Reason.Should().Be(SupervisorInterventionRejectionReason.CheckpointRevisionRegression);
        (await registry.AcceptAsync(CreateSupervisor(), CreateIntervention("new", new SupervisorAction.Approve(), 3), TestContext.Current.CancellationToken)).ExpectedRevision.Should().Be(3);
    }

    [Fact]
    public async Task Activation_cannot_refresh_or_authorize_after_decision()
    {
        var registry = await CreateRegistry();
        await registry.AcceptAsync(CreateSupervisor(), CreateIntervention(), TestContext.Current.CancellationToken);
        var refresh = () => registry.ActivateAsync(CreateSupervisor(), CreateWaitingProgress(3), TestContext.Current.CancellationToken).AsTask();
        var newSupervisor = new SupervisorIdentity("authority-2", "supervisor-2");
        var authorize = () => registry.ActivateAsync(newSupervisor, CreateWaitingProgress(), TestContext.Current.CancellationToken).AsTask();
        (await refresh.Should().ThrowAsync<SupervisorInterventionRejectedException>()).Which.Reason.Should().Be(SupervisorInterventionRejectionReason.CheckpointAlreadyDecided);
        (await authorize.Should().ThrowAsync<SupervisorInterventionRejectedException>()).Which.Reason.Should().Be(SupervisorInterventionRejectionReason.CheckpointAlreadyDecided);
    }

    [Fact]
    public async Task Activation_caps_authorized_supervisors_per_checkpoint()
    {
        var registry = await CreateRegistry();
        for (var index = 2; index <= 32; index++)
        {
            await registry.ActivateAsync(
                new SupervisorIdentity($"authority-{index}", $"supervisor-{index}"),
                CreateWaitingProgress(),
                TestContext.Current.CancellationToken);
        }

        var overLimit = () => registry.ActivateAsync(
            new SupervisorIdentity("authority-33", "supervisor-33"),
            CreateWaitingProgress(),
            TestContext.Current.CancellationToken).AsTask();

        (await overLimit.Should().ThrowAsync<SupervisorInterventionRejectedException>())
            .Which.Reason.Should().Be(SupervisorInterventionRejectionReason.AuthorizedSupervisorLimitExceeded);
    }

    [Fact]
    public async Task Cancellation_is_honored_by_activation_and_acceptance()
    {
        var registry = new InMemorySupervisorInterventionAcceptanceRegistry(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await ((Func<Task>)(() => registry.ActivateAsync(CreateSupervisor(), CreateWaitingProgress(), cancellation.Token).AsTask())).Should().ThrowAsync<OperationCanceledException>();
        var active = await CreateRegistry(); await ((Func<Task>)(() => active.AcceptAsync(CreateSupervisor(), CreateIntervention(), cancellation.Token).AsTask())).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Wake_hints_are_bounded_non_authorizing_prose()
    {
        var hint = new WakeHint(new DelegationId(Id(1)), new SupervisorCheckpointId(Id(10)), " attention\r\nrequested ", 2, DateTimeOffset.Parse("2026-01-01T00:05:00Z"));
        hint.Reason.Should().Be("attention\nrequested"); hint.ExpiresAt.Should().Be(DateTimeOffset.Parse("2026-01-01T00:05:00Z"));
    }

    [Fact]
    public void Acceptance_receipts_reject_empty_identity()
    {
        var fingerprint = SupervisorInterventionIdentity.Compute(CreateIntervention());
        var act = () => new SupervisorInterventionAcceptance(Guid.Empty, new DelegationId(Id(1)), new SupervisorCheckpointId(Id(10)), 2, CreateSupervisor(), fingerprint, true);
        act.Should().Throw<ArgumentException>().WithParameterName("receiptId");
    }

    private static async Task<InMemorySupervisorInterventionAcceptanceRegistry> CreateRegistry()
    {
        var registry = new InMemorySupervisorInterventionAcceptanceRegistry(); await registry.ActivateAsync(CreateSupervisor(), CreateWaitingProgress(), TestContext.Current.CancellationToken); return registry;
    }
    private static SupervisorIdentity CreateSupervisor() => new("authority-1", "supervisor-1");
    private static SupervisorIntervention CreateIntervention(string key = "decision-1", SupervisorAction? action = null, long revision = 2) => new(new DelegationId(Id(1)), new SupervisorCheckpointId(Id(10)), key, revision, action ?? new SupervisorAction.Approve("approve change"));
    private static DelegationProgress CreateWaitingProgress(long revision = 2) => new(new DelegationId(Id(1)), DelegationState.WaitingForSupervisor, revision, [], [], 0, 0, DateTimeOffset.Parse("2026-01-01T00:00:00Z"), new SupervisorCheckpointDescriptor(new SupervisorCheckpointId(Id(10)), new HongxianSessionReference("session-1"), new DelegationId(Id(1)), WorkflowPlanRevisionReference.BuiltInPreset("Implement", "1"), new WorkflowRunExecutionReference("zhinu", "run-1", "epoch-1"), new StructuralNodeReference("implement"), new NodeGenerationId(Id(11)), revision, true));
    private static Guid Id(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:D12}");
}
