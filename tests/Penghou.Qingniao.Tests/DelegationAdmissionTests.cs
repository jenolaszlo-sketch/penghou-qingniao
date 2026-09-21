using FluentAssertions;

namespace Penghou.Qingniao.Tests;

public sealed class DelegationAdmissionTests
{
    [Fact]
    public void Admitted_decision_carries_no_reason()
    {
        var decision = DelegationAdmissionDecision.Admitted();

        decision.IsAdmitted.Should().BeTrue();
        decision.Status.Should().Be(DelegationAdmissionStatus.Admitted);
        decision.Reason.Should().BeNull();
    }

    [Fact]
    public void Reject_requires_a_non_admitted_status_and_a_reason()
    {
        var decision = DelegationAdmissionDecision.Reject(DelegationAdmissionStatus.Unauthorized, "caller is unknown");

        decision.IsAdmitted.Should().BeFalse();
        decision.Reason.Should().Be("caller is unknown");

        var admitted = () => DelegationAdmissionDecision.Reject(DelegationAdmissionStatus.Admitted, "reason");
        admitted.Should().Throw<ArgumentException>();
        var empty = () => DelegationAdmissionDecision.Reject(DelegationAdmissionStatus.Rejected, " ");
        empty.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Admission_context_validates_the_request()
    {
        var caller = new DelegationCallerScope("caller-1");
        var request = CreateRequest("admission-context");

        var context = new DelegationAdmissionContext(caller, request);

        context.Caller.Should().BeSameAs(caller);
        context.Request.Should().BeSameAs(request);
    }

    [Fact]
    public void Coordinator_admits_without_a_host_verifier()
    {
        var coordinator = CreateCoordinator(verifier: null);
        var request = CreateRequest("admit-default");

        var act = () => coordinator.AcceptAsync(Caller(), request, TestContext.Current.CancellationToken).AsTask();

        act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Coordinator_rejects_when_the_host_verifier_rejects()
    {
        var coordinator = CreateCoordinator(
            verifier: new StubAdmissionVerifier(
                DelegationAdmissionDecision.Reject(DelegationAdmissionStatus.Unauthorized, "not this caller")));

        var act = () => coordinator.AcceptAsync(Caller(), CreateRequest("rejected"), TestContext.Current.CancellationToken).AsTask();

        (await act.Should().ThrowAsync<DelegationAdmissionException>())
            .Which.Status.Should().Be(DelegationAdmissionStatus.Unauthorized);
    }

    [Fact]
    public async Task Coordinator_accepts_when_the_host_verifier_admits()
    {
        var verifier = new StubAdmissionVerifier(DelegationAdmissionDecision.Admitted());
        var coordinator = CreateCoordinator(verifier: verifier);

        var accepted = await coordinator.AcceptAsync(Caller(), CreateRequest("admitted"), TestContext.Current.CancellationToken);

        accepted.IsNew.Should().BeTrue();
        verifier.Seen.Should().ContainSingle();
    }

    private static DelegationCallerScope Caller() => new("caller-1");

    private static DelegationRequest CreateRequest(string requestKey) => new(
        requestKey,
        "Do the work",
        "admission-provider",
        new WorkspaceReference("local", "workspace", "revision"),
        ["Done"],
        [],
        new DelegationBudget(MaximumWorkerCalls: 4, MaximumRetries: 1));

    private static InMemoryDelegationCoordinator CreateCoordinator(IDelegationAdmissionVerifier? verifier)
    {
        var descriptor = new ProviderDescriptor("admission-provider", [new CapabilityDescriptor("agent.execute", 1)]);
        var providers = new InMemoryProviderRegistry();
        providers.Register(descriptor);
        return new InMemoryDelegationCoordinator(
            new InMemoryDelegationAcceptanceRegistry(),
            verifier,
            providers,
            new InMemoryExternalOperationProviderCatalog(),
            now: () => DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
    }

    private sealed class StubAdmissionVerifier(DelegationAdmissionDecision decision) : IDelegationAdmissionVerifier
    {
        public List<DelegationAdmissionContext> Seen { get; } = [];

        public DelegationAdmissionDecision Verify(DelegationAdmissionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            Seen.Add(context);
            return decision;
        }
    }
}
