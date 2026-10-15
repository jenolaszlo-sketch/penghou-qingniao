using FluentAssertions;
using Penghou.Hufu;

namespace Penghou.Qingniao.Hufu.Tests;

/// <summary>
/// AE-6.2: the Hufu-backed start gate binds the exact start (delegation,
/// provider, revision) to current Hufu authority resolved through the host's
/// durable delegation binding. Unknown bindings deny; malformed provider
/// identity denies; narrowing is respected per attempt; nothing is cached.
/// </summary>
public sealed class HufuProviderStartAuthorizerTests
{
    [Fact]
    public async Task Permitted_start_builds_exact_request_with_snapshot_binding()
    {
        AuthorityRequest? seen = null;
        var authority = new AuthorityFake((request, _) =>
        {
            seen = request;
            return ValueTask.FromResult(Permit(request));
        });
        var authorizer = new HufuProviderStartAuthorizer(
            authority, "marang", (_, _) => ValueTask.FromResult<(string, string)?>(("tester", "workspace")));
        var ct = TestContext.Current.CancellationToken;
        var id = new DelegationId(Guid.NewGuid());

        var outcome = await authorizer.AuthorizeAsync(
            new ProviderStartAuthorizationContext(id, "workspace", "codex", 3), ct);

        outcome.Status.Should().Be(ProviderStartAuthorizationStatus.Permitted);
        outcome.EvidenceId.Should().NotBeNullOrWhiteSpace();
        seen.Should().NotBeNull();
        seen!.Action.Should().Be(AuthorityAction.ExecuteProcess);
        seen.WorkspaceId.Should().Be("workspace");
        seen.RelativePath.Should().Be($"delegation/{id.Value:D}/execution/codex");
        seen.Context.TenantId.Should().Be("marang");
        seen.Context.SubjectId.Should().Be("tester");
        seen.Context.RunId.Should().Be(id.Value.ToString("D"));
        seen.Context.RevisionId.Should().Be(HufuProviderStartAuthorizer.Revision);
        seen.Context.FenceId.Should().Be(HufuProviderStartAuthorizer.Fence);
    }

    [Fact]
    public async Task Unknown_binding_denies()
    {
        var authority = new AuthorityFake();
        var authorizer = new HufuProviderStartAuthorizer(
            authority, "marang", (_, _) => ValueTask.FromResult<(string, string)?>(null));
        var ct = TestContext.Current.CancellationToken;

        var outcome = await authorizer.AuthorizeAsync(
            new ProviderStartAuthorizationContext(new DelegationId(Guid.NewGuid()), "workspace", "codex", 0), ct);

        outcome.Status.Should().Be(ProviderStartAuthorizationStatus.Denied);
        outcome.ReasonCode.Should().Be("qingniao.start-binding-unknown");
        authority.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Noncanonical_provider_denies_without_authority_contact()
    {
        var authority = new AuthorityFake();
        var authorizer = new HufuProviderStartAuthorizer(
            authority, "marang", (_, _) => ValueTask.FromResult<(string, string)?>(("tester", "workspace")));
        var ct = TestContext.Current.CancellationToken;

        var outcome = await authorizer.AuthorizeAsync(
            new ProviderStartAuthorizationContext(new DelegationId(Guid.NewGuid()), "workspace", "Codex", 0), ct);

        outcome.Status.Should().Be(ProviderStartAuthorizationStatus.Denied);
        outcome.ReasonCode.Should().Be("qingniao.start-provider-invalid");
        authority.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Denied_authority_denies_with_hufu_reason()
    {
        var authority = new AuthorityFake((request, _) => ValueTask.FromResult(Deny(request)));
        var authorizer = new HufuProviderStartAuthorizer(
            authority, "marang", (_, _) => ValueTask.FromResult<(string, string)?>(("tester", "workspace")));
        var ct = TestContext.Current.CancellationToken;

        var outcome = await authorizer.AuthorizeAsync(
            new ProviderStartAuthorizationContext(new DelegationId(Guid.NewGuid()), "workspace", "codex", 0), ct);

        outcome.Status.Should().Be(ProviderStartAuthorizationStatus.Denied);
    }

    [Fact]
    public async Task Unavailable_authority_is_unavailable_not_permit()
    {
        var authority = new AuthorityFake((request, _) => ValueTask.FromResult(Unavailable(request)));
        var authorizer = new HufuProviderStartAuthorizer(
            authority, "marang", (_, _) => ValueTask.FromResult<(string, string)?>(("tester", "workspace")));
        var ct = TestContext.Current.CancellationToken;

        var outcome = await authorizer.AuthorizeAsync(
            new ProviderStartAuthorizationContext(new DelegationId(Guid.NewGuid()), "workspace", "codex", 0), ct);

        outcome.Status.Should().Be(ProviderStartAuthorizationStatus.Unavailable);
    }

    [Fact]
    public async Task Narrowing_between_attempts_is_respected()
    {
        var calls = 0;
        var authority = new AuthorityFake((request, _) =>
        {
            calls++;
            return ValueTask.FromResult(calls == 1 ? Permit(request) : Deny(request));
        });
        var authorizer = new HufuProviderStartAuthorizer(
            authority, "marang", (_, _) => ValueTask.FromResult<(string, string)?>(("tester", "workspace")));
        var ct = TestContext.Current.CancellationToken;
        var context = new ProviderStartAuthorizationContext(new DelegationId(Guid.NewGuid()), "workspace", "codex", 0);

        (await authorizer.AuthorizeAsync(context, ct)).Status.Should().Be(ProviderStartAuthorizationStatus.Permitted);
        (await authorizer.AuthorizeAsync(context, ct)).Status.Should().Be(ProviderStartAuthorizationStatus.Denied);
        authority.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Binding_resolver_failure_is_unavailable()
    {
        var authority = new AuthorityFake();
        var authorizer = new HufuProviderStartAuthorizer(
            authority, "marang", new ThrowingResolver().Resolve);
        var ct = TestContext.Current.CancellationToken;

        var outcome = await authorizer.AuthorizeAsync(
            new ProviderStartAuthorizationContext(new DelegationId(Guid.NewGuid()), "workspace", "codex", 0), ct);

        outcome.Status.Should().Be(ProviderStartAuthorizationStatus.Unavailable);
        authority.Requests.Should().BeEmpty();
    }

    private static AuthorityRequestAuthorization Permit(AuthorityRequest request) =>
        new(request, AuthorityStatus.Permit,
            new AuthorityDecision(AuthorityStatus.Permit, "authority.permit", "snapshot-1", "evaluator-1",
                new string('a', 64)), true);

    private static AuthorityRequestAuthorization Deny(AuthorityRequest request) =>
        new(request, AuthorityStatus.Deny,
            new AuthorityDecision(AuthorityStatus.Deny, "authority.denied", "snapshot-1", "evaluator-1",
                new string('b', 64)), true);

    private static AuthorityRequestAuthorization Unavailable(AuthorityRequest request) =>
        new(request, AuthorityStatus.Unavailable, null, false);

    private sealed class ThrowingResolver
    {
        public ValueTask<(string, string)?> Resolve(DelegationId _, CancellationToken __) =>
            throw new InvalidOperationException("store down");
    }

    private sealed class AuthorityFake(
        Func<AuthorityRequest, CancellationToken, ValueTask<AuthorityRequestAuthorization>>? authorize = null)
        : IAuthorityRequestAuthorizer
    {
        public List<AuthorityRequest> Requests { get; } = [];

        public async ValueTask<AuthorityRequestAuthorization> AuthorizeAsync(AuthorityRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return authorize is null
                ? Permit(request)
                : await authorize(request, cancellationToken).ConfigureAwait(false);
        }
    }
}
