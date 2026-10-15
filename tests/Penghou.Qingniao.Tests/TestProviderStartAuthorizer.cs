namespace Penghou.Qingniao.Tests;

/// <summary>
/// Test-only provider-start authorizers. NOT production code. Existing
/// execution tests unrelated to start authority keep their behavior by
/// allowing all starts explicitly, while every runtime construction still
/// supplies an explicit authorizer. Deny-all/unavailable variants prove
/// wiring in the start-authority qualification.
/// </summary>
internal sealed class TestProviderStartAuthorizer : IProviderStartAuthorizer
{
    private readonly ProviderStartAuthorizationOutcome _outcome;

    public static TestProviderStartAuthorizer AllowAll() => new(new ProviderStartAuthorizationOutcome(
        ProviderStartAuthorizationStatus.Permitted, "test.permitted"));

    public static TestProviderStartAuthorizer DenyAll() => new(new ProviderStartAuthorizationOutcome(
        ProviderStartAuthorizationStatus.Denied, "test.denied"));

    public static TestProviderStartAuthorizer Unavailable() => new(new ProviderStartAuthorizationOutcome(
        ProviderStartAuthorizationStatus.Unavailable, "test.unavailable"));

    private TestProviderStartAuthorizer(ProviderStartAuthorizationOutcome outcome) => _outcome = outcome;

    public ValueTask<ProviderStartAuthorizationOutcome> AuthorizeAsync(
        ProviderStartAuthorizationContext context, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_outcome);
}
