using FluentAssertions;

namespace Penghou.Qingniao.Tests;

public sealed class DelegationAuthorityContractsTests
{
    private static readonly DateTimeOffset NotBefore = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ExpiresAt = NotBefore.AddHours(1);

    private static RequestedAuthority Authority(string action = "ReadFile") => new(
        [action],
        new RequestedAuthorityScope("workspace", "src/service", RequestedAuthorityScopeKind.Subtree),
        [new RequestedAuthorityScope("workspace", "src/service/secret", RequestedAuthorityScopeKind.Subtree)],
        NotBefore,
        ExpiresAt);

    [Fact]
    public void Request_exposes_authority_as_data_only()
    {
        var request = Request(parentGrantId: "grant-parent", requestedAuthority: Authority());

        request.ParentGrantId.Should().Be("grant-parent");
        request.RequestedAuthority.Should().NotBeNull();
        request.RequestedAuthority!.Actions.Should().Equal("ReadFile");
        request.RequestedAuthority.Scope.WorkspaceId.Should().Be("workspace");
        request.RequestedAuthority.Scope.Kind.Should().Be(RequestedAuthorityScopeKind.Subtree);
        request.RequestedAuthority.Exclusions.Should().HaveCount(1);
    }

    [Fact]
    public void Request_omitted_authority_fields_stay_null()
    {
        var request = Request();

        request.ParentGrantId.Should().BeNull();
        request.RequestedAuthority.Should().BeNull();
    }

    [Fact]
    public void Requested_authority_snapshots_its_collections()
    {
        var actions = new List<string> { "ReadFile" };
        var exclusions = new List<RequestedAuthorityScope>
        {
            new("workspace", "src/secret", RequestedAuthorityScopeKind.Subtree)
        };
        var authority = new RequestedAuthority(
            actions,
            new RequestedAuthorityScope("workspace", "src", RequestedAuthorityScopeKind.Subtree),
            exclusions,
            NotBefore,
            ExpiresAt);

        actions.Add("WriteFile");
        exclusions.Clear();

        authority.Actions.Should().Equal("ReadFile");
        authority.Exclusions.Should().HaveCount(1);
    }

    [Fact]
    public void Requested_authority_requires_a_non_empty_validity_interval()
    {
        var act = () => new RequestedAuthority(
            ["ReadFile"],
            new RequestedAuthorityScope("workspace", "src", RequestedAuthorityScopeKind.Subtree),
            [],
            ExpiresAt,
            NotBefore);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Requested_authority_rejects_duplicate_or_empty_actions()
    {
        var duplicate = () => new RequestedAuthority(
            ["ReadFile", "ReadFile"],
            new RequestedAuthorityScope("workspace", "src", RequestedAuthorityScopeKind.Subtree),
            [], NotBefore, ExpiresAt);
        var empty = () => new RequestedAuthority(
            [],
            new RequestedAuthorityScope("workspace", "src", RequestedAuthorityScopeKind.Subtree),
            [], NotBefore, ExpiresAt);

        duplicate.Should().Throw<ArgumentException>();
        empty.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Validator_accepts_a_bounded_authority_request()
    {
        var request = Request(parentGrantId: "grant-parent", requestedAuthority: Authority());

        var act = () => DelegationRequestValidator.Validate(request);

        act.Should().NotThrow();
    }

    [Fact]
    public void Non_canonical_parent_grant_identity_is_rejected_at_construction()
    {
        var act = () => Request(parentGrantId: " grant-parent");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Qingniao_does_not_canonicalize_authority_paths()
    {
        // Canonicalization, containment, and delegability belong to Hufu; a
        // transport-shape path with traversal segments is carried verbatim.
        var authority = new RequestedAuthority(
            ["ReadFile"],
            new RequestedAuthorityScope("workspace", "src/../Secret", RequestedAuthorityScopeKind.Subtree),
            [],
            NotBefore,
            ExpiresAt);

        authority.Scope.RelativePath.Should().Be("src/../Secret");
    }

    [Fact]
    public void Scope_kinds_mirror_the_hufu_authority_scope_kinds()
    {
        ((int)RequestedAuthorityScopeKind.Exact).Should().Be(0);
        ((int)RequestedAuthorityScopeKind.Subtree).Should().Be(1);
    }

    [Fact]
    public void Delegation_content_identity_excludes_requested_authority()
    {
        // Delegation identity is deliberately independent of authority intent:
        // authority is versioned by Hufu's derivation identity, so the same
        // delegation can serve a distinct derivation without a content conflict.
        var without = Request();
        var first = Request(parentGrantId: "grant-parent", requestedAuthority: Authority());
        var second = Request(parentGrantId: "grant-other", requestedAuthority: Authority("WriteFile"));

        DelegationRequestIdentity.Compute(first).Should().Be(DelegationRequestIdentity.Compute(without));
        DelegationRequestIdentity.Compute(first).Should().Be(DelegationRequestIdentity.Compute(second));
        DelegationRequestIdentity.Canonicalize(first).Should().NotContain("grant-parent");
    }

    private static DelegationRequest Request(
        string? parentGrantId = null,
        RequestedAuthority? requestedAuthority = null) => new(
        "request-1",
        "Do the work",
        "test-provider",
        new WorkspaceReference("local", "project", "revision"),
        ["Criteria"],
        ["Constraint"],
        new DelegationBudget(MaximumDuration: TimeSpan.FromSeconds(12)),
        parentGrantId: parentGrantId,
        requestedAuthority: requestedAuthority);
}
