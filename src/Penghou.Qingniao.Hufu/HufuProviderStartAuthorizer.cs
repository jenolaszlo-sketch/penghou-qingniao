using Penghou.Hufu;

namespace Penghou.Qingniao.Hufu;

/// <summary>
/// Host-owned Hufu-backed provider-start authority. There is no credential at
/// pump time: identity provenance comes from the host's durable delegation
/// binding (subject and workspace resolved by delegation id from host-owned
/// state, never from provider labels), and permission comes from a fresh Hufu
/// current-authority decision per start attempt. The two must not be confused.
/// </summary>
public sealed class HufuProviderStartAuthorizer : IProviderStartAuthorizer
{
    /// <summary>Revision bound into every start context. Fixed so restarts evaluate the same authority subject.</summary>
    public const string Revision = "g0";

    /// <summary>
    /// Fence bound into every start context. Distinct from acceptance,
    /// resource, and workflow fences so decision domains never share a context.
    /// </summary>
    public const string Fence = "provider-start";

    private readonly IAuthorityRequestAuthorizer _authority;
    private readonly string _tenantId;
    private readonly Func<DelegationId, CancellationToken, ValueTask<(string SubjectId, string WorkspaceId)?>> _resolveBinding;

    /// <summary>
    /// Initializes a Hufu-backed start authorizer. The tenant is host
    /// configuration; the binding resolver reads host-owned durable delegation
    /// state. Nothing is taken from provider-controlled labels.
    /// </summary>
    public HufuProviderStartAuthorizer(
        IAuthorityRequestAuthorizer authority,
        string tenantId,
        Func<DelegationId, CancellationToken, ValueTask<(string SubjectId, string WorkspaceId)?>> resolveBinding)
    {
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        if (string.IsNullOrWhiteSpace(tenantId)) throw new ArgumentException("A tenant identity is required.", nameof(tenantId));
        _tenantId = tenantId;
        _resolveBinding = resolveBinding ?? throw new ArgumentNullException(nameof(resolveBinding));
    }

    /// <summary>
    /// Authorizes one provider start against current Hufu authority. Unknown
    /// bindings deny; malformed provider identity denies; authority failure
    /// closes without starting.
    /// </summary>
    public async ValueTask<ProviderStartAuthorizationOutcome> AuthorizeAsync(
        ProviderStartAuthorizationContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context is null || !context.Valid)
            return new ProviderStartAuthorizationOutcome(
                ProviderStartAuthorizationStatus.Denied, "qingniao.start-context-invalid");

        (string SubjectId, string WorkspaceId)? binding;
        try
        {
            binding = await _resolveBinding(context.DelegationId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            return new ProviderStartAuthorizationOutcome(
                ProviderStartAuthorizationStatus.Unavailable, "qingniao.start-binding-unavailable");
        }

        if (binding is null)
            return new ProviderStartAuthorizationOutcome(
                ProviderStartAuthorizationStatus.Denied, "qingniao.start-binding-unknown");

        AuthorityRequest request;
        try
        {
            // The provider id selects the executable that will actually run:
            // it must already be canonical. Folding here would authorize one
            // spelling while the coordinator starts another.
            var provider = AuthorityValidation.NormalizePath(context.Provider);
            if (provider != context.Provider)
                return new ProviderStartAuthorizationOutcome(
                    ProviderStartAuthorizationStatus.Denied, "qingniao.start-provider-invalid");
            var hufuContext = new AuthenticatedAuthorityContext(_tenantId, binding.Value.SubjectId,
                context.DelegationId.Value.ToString("D"), Revision, Fence);
            request = new AuthorityRequest(hufuContext, AuthorityAction.ExecuteProcess,
                binding.Value.WorkspaceId, $"delegation/{context.DelegationId.Value:D}/execution/{provider}",
                Guid.NewGuid().ToString("N"));
        }
        catch (ArgumentException)
        {
            return new ProviderStartAuthorizationOutcome(
                ProviderStartAuthorizationStatus.Denied, "qingniao.start-request-invalid");
        }

        AuthorityRequestAuthorization result;
        try
        {
            result = await _authority.AuthorizeAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            return new ProviderStartAuthorizationOutcome(
                ProviderStartAuthorizationStatus.Unavailable, "qingniao.start-authority-unavailable");
        }

        if (result.IsAuthorized)
            return new ProviderStartAuthorizationOutcome(ProviderStartAuthorizationStatus.Permitted,
                result.Decision!.ReasonCode, result.Decision.SnapshotIdentity);
        return result.Status == AuthorityStatus.Deny
            ? new ProviderStartAuthorizationOutcome(ProviderStartAuthorizationStatus.Denied,
                result.Decision?.ReasonCode ?? "qingniao.start-denied")
            : new ProviderStartAuthorizationOutcome(ProviderStartAuthorizationStatus.Unavailable,
                result.Decision?.ReasonCode ?? "qingniao.start-unavailable");
    }
}
