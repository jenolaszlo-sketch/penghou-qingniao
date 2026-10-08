using Penghou.Hufu;

namespace Penghou.Qingniao.Hufu;

/// <summary>
/// Host-owned Hufu-backed authority preflight for Qingniao delegations. The
/// host supplies the issuing actor, the parent execution context, the child
/// execution-context factory, and the derivation store; Qingniao supplies only
/// the delegation identity, generation, and authority labels. The adapter maps
/// Qingniao requested authority to Hufu strictly and boringly, preserves Hufu
/// Deny/Unavailable distinctly, and returns an opaque execution attachment
/// carrying only the child execution context. It keeps no grant cache:
/// same-generation retries converge on the same child through Hufu's
/// derivation idempotency.
/// </summary>
public sealed class HufuDelegationAuthorityPreflight : IDelegationAuthorityPreflight
{
    private readonly IAuthorityDerivationStore _store;
    private readonly AuthorityStoreActor _actor;
    private readonly AuthenticatedAuthorityContext _parentContext;
    private readonly Func<string, string, AuthenticatedAuthorityContext> _childContextFactory;

    /// <summary>
    /// Initializes a Hufu-backed preflight. All host context is trusted host
    /// configuration; nothing authenticated is taken from Qingniao labels.
    /// </summary>
    public HufuDelegationAuthorityPreflight(
        IAuthorityDerivationStore store,
        AuthorityStoreActor actor,
        AuthenticatedAuthorityContext parentContext,
        Func<string, string, AuthenticatedAuthorityContext> childContextFactory)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(parentContext);
        ArgumentNullException.ThrowIfNull(childContextFactory);
        _store = store;
        _actor = actor;
        _parentContext = parentContext;
        _childContextFactory = childContextFactory;
    }

    /// <summary>
    /// Derives the requested child authority through Hufu. Mapping failures
    /// fail closed as Deny; an unreachable store fails closed as Unavailable.
    /// </summary>
    public async ValueTask<DelegationAuthorityPreflightDecision> PreflightAsync(
        DelegationAuthorityPreflightContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        Penghou.Hufu.RequestedAuthority requested;
        try
        {
            requested = MapRequestedAuthority(context.RequestedAuthority);
        }
        catch (ArgumentException exception)
        {
            return DelegationAuthorityPreflightDecision.Deny(
                $"The requested authority is not a strict Hufu authority envelope: {exception.Message}");
        }

        var delegationId = context.DelegationId.Value.ToString("D");
        var generation = context.Generation.Value.ToString("D");

        AuthenticatedAuthorityContext childContext;
        try
        {
            childContext = _childContextFactory(delegationId, generation)
                ?? throw new ArgumentException("The host child-context factory returned no context.");
            if (!AuthorityValidation.ValidContext(childContext))
            {
                throw new ArgumentException("The host child context is not a bounded authenticated context.");
            }
        }
        catch (ArgumentException exception)
        {
            return DelegationAuthorityPreflightDecision.Deny(
                $"The host child execution context is invalid: {exception.Message}");
        }

        AuthorityDerivationCommand command;
        try
        {
            command = new AuthorityDerivationCommand(
                _actor,
                _parentContext,
                context.ParentGrantId,
                childContext,
                delegationId,
                generation,
                requested,
                Guid.NewGuid().ToString("N"));
        }
        catch (ArgumentException exception)
        {
            return DelegationAuthorityPreflightDecision.Deny(
                $"The derivation command is malformed: {exception.Message}");
        }

        AuthorityGrantDerivationResult result;
        try
        {
            result = await _store.DeriveAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return DelegationAuthorityPreflightDecision.Unavailable(
                $"The Hufu derivation store was unreachable: {exception.Message}");
        }

        return result.Status switch
        {
            AuthorityStatus.Permit => DelegationAuthorityPreflightDecision.Permit(
                new DelegationExecutionAttachment(
                    HufuExecutionAttachment.Kind,
                    HufuExecutionAttachment.Encode(childContext))),
            AuthorityStatus.Deny => DelegationAuthorityPreflightDecision.Deny(
                result.ReasonCode ?? "hufu.derivation-denied"),
            _ => DelegationAuthorityPreflightDecision.Unavailable(
                result.ReasonCode ?? "hufu.derivation-unavailable"),
        };
    }

    private static Penghou.Hufu.RequestedAuthority MapRequestedAuthority(RequestedAuthority requested)
    {
        ArgumentNullException.ThrowIfNull(requested);
        var actions = new List<AuthorityAction>(requested.Actions.Count);
        foreach (var name in requested.Actions)
        {
            if (!Enum.TryParse<AuthorityAction>(name, ignoreCase: false, out var action)
                || !Enum.IsDefined(action))
            {
                throw new ArgumentException($"Unknown authority action '{name}'.", nameof(requested));
            }

            actions.Add(action);
        }

        return new Penghou.Hufu.RequestedAuthority(
            actions,
            MapScope(requested.Scope),
            requested.Exclusions.Select(MapScope).ToList(),
            requested.NotBefore,
            requested.ExpiresAt);
    }

    private static AuthorityScope MapScope(RequestedAuthorityScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var kind = scope.Kind switch
        {
            RequestedAuthorityScopeKind.Exact => AuthorityScopeKind.Exact,
            RequestedAuthorityScopeKind.Subtree => AuthorityScopeKind.Subtree,
            _ => throw new ArgumentException($"Unknown authority scope kind '{scope.Kind}'.", nameof(scope)),
        };

        return new AuthorityScope(scope.WorkspaceId, scope.RelativePath, kind);
    }
}
