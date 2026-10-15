namespace Penghou.Qingniao;

/// <summary>Outcome of the mandatory provider-start authority gate for one start attempt.</summary>
public enum ProviderStartAuthorizationStatus
{
    /// <summary>The start attempt is permitted under current authority.</summary>
    Permitted = 0,
    /// <summary>Current authority denies the start attempt. No provider side effect may occur.</summary>
    Denied = 1,
    /// <summary>Authority could not be evaluated. No provider side effect may occur; the attempt may be retried.</summary>
    Unavailable = 2,
}

/// <summary>One provider-start authorization attempt, bound to the exact start that would execute.</summary>
public sealed record ProviderStartAuthorizationContext(
    DelegationId DelegationId,
    string Workspace,
    string Provider,
    long Revision)
{
    /// <summary>
    /// Whether the context is well-formed. Workspace and provider arrive from
    /// host-resolved delegation state, never raw tool input; malformed values
    /// fail the gate closed.
    /// </summary>
    public bool Valid =>
        DelegationId.Value != Guid.Empty && IsToken(Workspace) && IsToken(Provider) && Revision >= 0;

    private static bool IsToken(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 512 && !value.Any(char.IsControl);
}

/// <summary>Typed start-gate outcome with a bounded reason and optional authority evidence reference.</summary>
public sealed record ProviderStartAuthorizationOutcome(
    ProviderStartAuthorizationStatus Status, string ReasonCode, string? EvidenceId = null);

/// <summary>
/// Mandatory per-attempt provider-start authority gate. Evaluated immediately
/// before every external provider start against current authority; never
/// cached across attempts. There is no permissive implementation: hosts
/// compose a real gate, and a missing gate fails closed at construction.
/// </summary>
public interface IProviderStartAuthorizer
{
    /// <summary>Evaluates one provider-start attempt against current authority.</summary>
    ValueTask<ProviderStartAuthorizationOutcome> AuthorizeAsync(
        ProviderStartAuthorizationContext context, CancellationToken cancellationToken = default);
}

/// <summary>Fail-closed start gate for hosts without composed start authority. It never permits.</summary>
public sealed class FailClosedProviderStartAuthorizer : IProviderStartAuthorizer
{
    /// <summary>Shared fail-closed instance.</summary>
    public static FailClosedProviderStartAuthorizer Instance { get; } = new();

    private FailClosedProviderStartAuthorizer()
    {
    }

    /// <summary>Always returns unavailable: start authority is not configured.</summary>
    public ValueTask<ProviderStartAuthorizationOutcome> AuthorizeAsync(
        ProviderStartAuthorizationContext context, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new ProviderStartAuthorizationOutcome(
            ProviderStartAuthorizationStatus.Unavailable, "qingniao.start-authority-not-configured"));
}
