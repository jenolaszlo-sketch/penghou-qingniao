using FluentAssertions;
using Penghou.Hufu;

namespace Penghou.Qingniao.Hufu.Tests;

/// <summary>
/// Proves the Hufu preflight adapter: strict boring requested-authority
/// mapping that fails closed, exact Hufu-to-Qingniao status preservation, an
/// opaque attachment carrying only the child execution context, and no adapter
/// grant cache (Hufu owns derivation idempotency).
/// </summary>
public sealed class HufuDelegationAuthorityPreflightTests
{
    private static readonly DateTimeOffset NotBefore = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ExpiresAt = NotBefore.AddHours(1);
    private static readonly AuthorityStoreActor Actor = new("tenant", "host", "session-1");
    private static readonly AuthenticatedAuthorityContext ParentContext =
        new("tenant", "supervisor", "run-parent", "rev", "fence");
    private static readonly AuthenticatedAuthorityContext ChildContext =
        new("tenant", "delegation", "run-child", "rev", "fence");

    [Fact]
    public async Task Permit_returns_a_resolvable_attachment_carrying_only_the_child_context()
    {
        var grant = new AuthorityGrant(
            "grant-child",
            [AuthorityAction.ReadFile],
            new AuthorityScope("workspace", "src/service", AuthorityScopeKind.Subtree),
            [],
            NotBefore,
            ExpiresAt);
        var store = new FakeDerivationStore(_ =>
            new AuthorityGrantDerivationResult(AuthorityStatus.Permit, grant, "grant-parent", "deriv-1", null));
        var preflight = CreatePreflight(store);
        var ct = TestContext.Current.CancellationToken;

        var decision = await preflight.PreflightAsync(CreateContext(), ct);

        decision.Status.Should().Be(DelegationAuthorityPreflightStatus.Permit);
        decision.ExecutionAttachment.Should().NotBeNull();
        decision.ExecutionAttachment!.Kind.Should().Be(HufuExecutionAttachment.Kind);
        HufuExecutionAttachment.TryResolve(decision.ExecutionAttachment, out var resolved).Should().BeTrue();
        resolved.Should().Be(ChildContext);
        store.Calls.Should().Be(1);
        store.LastCommand!.ParentGrantId.Should().Be("grant-parent");
        store.LastCommand.Requested.Actions.Should().Equal(AuthorityAction.ReadFile);
    }

    [Fact]
    public async Task Deny_preserves_the_hufu_reason_without_collapsing()
    {
        var store = new FakeDerivationStore(_ =>
            new AuthorityGrantDerivationResult(AuthorityStatus.Deny, null, "grant-parent", "deriv-1", "hufu.not-contained"));
        var preflight = CreatePreflight(store);
        var ct = TestContext.Current.CancellationToken;

        var decision = await preflight.PreflightAsync(CreateContext(), ct);

        decision.Status.Should().Be(DelegationAuthorityPreflightStatus.Deny);
        decision.Reason.Should().Be("hufu.not-contained");
        decision.ExecutionAttachment.Should().BeNull();
    }

    [Fact]
    public async Task Unavailable_preserves_its_distinct_status()
    {
        var store = new FakeDerivationStore(_ =>
            new AuthorityGrantDerivationResult(AuthorityStatus.Unavailable, null, "grant-parent", "deriv-1", "hufu.store-behind"));
        var preflight = CreatePreflight(store);
        var ct = TestContext.Current.CancellationToken;

        var decision = await preflight.PreflightAsync(CreateContext(), ct);

        decision.Status.Should().Be(DelegationAuthorityPreflightStatus.Unavailable);
        decision.Reason.Should().Be("hufu.store-behind");
        decision.ExecutionAttachment.Should().BeNull();
    }

    [Fact]
    public async Task Unknown_action_fails_closed_as_deny_without_calling_the_store()
    {
        var store = new FakeDerivationStore(_ =>
            new AuthorityGrantDerivationResult(AuthorityStatus.Permit, null, "grant-parent", "deriv-1", null));
        var preflight = CreatePreflight(store);
        var requested = new Penghou.Qingniao.RequestedAuthority(
            ["DeleteEverything"],
            new RequestedAuthorityScope("workspace", "src", RequestedAuthorityScopeKind.Subtree),
            [],
            NotBefore,
            ExpiresAt);
        var ct = TestContext.Current.CancellationToken;

        var decision = await preflight.PreflightAsync(CreateContext(requested), ct);

        decision.Status.Should().Be(DelegationAuthorityPreflightStatus.Deny);
        decision.Reason.Should().Contain("Unknown authority action");
        store.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Exclusion_outside_scope_fails_closed_as_deny_without_calling_the_store()
    {
        // Qingniao carries the envelope verbatim; Hufu rejects the
        // non-contained exclusion and the adapter refuses rather than narrowing.
        var store = new FakeDerivationStore(_ =>
            new AuthorityGrantDerivationResult(AuthorityStatus.Permit, null, "grant-parent", "deriv-1", null));
        var preflight = CreatePreflight(store);
        var requested = new Penghou.Qingniao.RequestedAuthority(
            ["ReadFile"],
            new RequestedAuthorityScope("workspace", "src/service", RequestedAuthorityScopeKind.Subtree),
            [new RequestedAuthorityScope("workspace", "src/other", RequestedAuthorityScopeKind.Subtree)],
            NotBefore,
            ExpiresAt);
        var ct = TestContext.Current.CancellationToken;

        var decision = await preflight.PreflightAsync(CreateContext(requested), ct);

        decision.Status.Should().Be(DelegationAuthorityPreflightStatus.Deny);
        store.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Unreachable_store_fails_closed_as_unavailable()
    {
        var store = new ThrowingDerivationStore();
        var preflight = CreatePreflight(store);
        var ct = TestContext.Current.CancellationToken;

        var decision = await preflight.PreflightAsync(CreateContext(), ct);

        decision.Status.Should().Be(DelegationAuthorityPreflightStatus.Unavailable);
        decision.ExecutionAttachment.Should().BeNull();
    }

    [Fact]
    public async Task Adapter_keeps_no_grant_cache_and_repeats_the_derivation_call()
    {
        var grant = new AuthorityGrant(
            "grant-child",
            [AuthorityAction.ReadFile],
            new AuthorityScope("workspace", "src/service", AuthorityScopeKind.Subtree),
            [],
            NotBefore,
            ExpiresAt);
        var store = new FakeDerivationStore(_ =>
            new AuthorityGrantDerivationResult(AuthorityStatus.Permit, grant, "grant-parent", "deriv-1", null));
        var preflight = CreatePreflight(store);
        var context = CreateContext();
        var ct = TestContext.Current.CancellationToken;

        var first = await preflight.PreflightAsync(context, ct);
        var second = await preflight.PreflightAsync(context, ct);

        first.Status.Should().Be(DelegationAuthorityPreflightStatus.Permit);
        second.Status.Should().Be(DelegationAuthorityPreflightStatus.Permit);
        first.ExecutionAttachment.Should().Be(second.ExecutionAttachment);
        store.Calls.Should().Be(2);
    }

    [Fact]
    public void TryResolve_rejects_foreign_kind_malformed_value_and_missing_attachment()
    {
        HufuExecutionAttachment.TryResolve(null, out var missing).Should().BeFalse();
        missing.Should().BeNull();

        var foreign = new DelegationExecutionAttachment("other.kind", HufuExecutionAttachment.Encode(ChildContext));
        HufuExecutionAttachment.TryResolve(foreign, out var wrongKind).Should().BeFalse();
        wrongKind.Should().BeNull();

        var malformed = new DelegationExecutionAttachment(HufuExecutionAttachment.Kind, "not-a-context");
        HufuExecutionAttachment.TryResolve(malformed, out var badValue).Should().BeFalse();
        badValue.Should().BeNull();
    }

    [Fact]
    public void Attachment_round_trip_is_exact_and_kind_scoped()
    {
        var value = HufuExecutionAttachment.Encode(ChildContext);

        HufuExecutionAttachment.Decode(value).Should().Be(ChildContext);

        var attachment = new DelegationExecutionAttachment(HufuExecutionAttachment.Kind, value);
        attachment.Kind.Should().Be("penghou.hufu.authority-context");
        HufuExecutionAttachment.TryResolve(attachment, out var resolved).Should().BeTrue();
        resolved.Should().Be(ChildContext);
    }

    private static HufuDelegationAuthorityPreflight CreatePreflight(IAuthorityDerivationStore store) =>
        new(store, Actor, ParentContext, (_, _) => ChildContext);

    private static DelegationAuthorityPreflightContext CreateContext(
        Penghou.Qingniao.RequestedAuthority? requested = null) =>
        new(
            new DelegationId(Guid.NewGuid()),
            new NodeGenerationId(Guid.NewGuid()),
            "grant-parent",
            requested ?? new Penghou.Qingniao.RequestedAuthority(
                ["ReadFile"],
                new RequestedAuthorityScope("workspace", "src/service", RequestedAuthorityScopeKind.Subtree),
                [],
                NotBefore,
                ExpiresAt));

    private sealed class FakeDerivationStore(
        Func<AuthorityDerivationCommand, AuthorityGrantDerivationResult> derive) : IAuthorityDerivationStore
    {
        public int Calls { get; private set; }
        public AuthorityDerivationCommand? LastCommand { get; private set; }

        public ValueTask<AuthorityGrantDerivationResult> DeriveAsync(
            AuthorityDerivationCommand command,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastCommand = command;
            return new(derive(command));
        }

        public ValueTask<AuthorityGrantLineage?> GetLineageAsync(
            string childGrantId,
            CancellationToken cancellationToken = default) =>
            new((AuthorityGrantLineage?)null);
    }

    private sealed class ThrowingDerivationStore : IAuthorityDerivationStore
    {
        public ValueTask<AuthorityGrantDerivationResult> DeriveAsync(
            AuthorityDerivationCommand command,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<AuthorityGrantDerivationResult>(
                new InvalidOperationException("Derivation store unavailable."));

        public ValueTask<AuthorityGrantLineage?> GetLineageAsync(
            string childGrantId,
            CancellationToken cancellationToken = default) =>
            new((AuthorityGrantLineage?)null);
    }
}
