namespace Penghou.Qingniao;

/// <summary>
/// Scope kind for requested authority. Order mirrors the Hufu authority scope
/// kinds so hosts translate one-to-one; Qingniao never interprets them.
/// </summary>
public enum RequestedAuthorityScopeKind
{
    /// <summary>Matches a single resource path exactly.</summary>
    Exact = 0,
    /// <summary>Matches a resource path and everything beneath it.</summary>
    Subtree = 1
}

/// <summary>
/// A workspace-relative scope shape for requested authority. Labels only:
/// Qingniao validates transport shape (bounded, non-empty identifiers) and
/// never decides canonicalization, containment, or authorization.
/// </summary>
public sealed record RequestedAuthorityScope
{
    /// <summary>
    /// Initializes a new instance of the RequestedAuthorityScope type.
    /// </summary>
    public RequestedAuthorityScope(string workspaceId, string relativePath, RequestedAuthorityScopeKind kind)
    {
        WorkspaceId = IdentityText.Require(workspaceId, nameof(workspaceId), 256);
        RelativePath = RequireAuthorityPath(relativePath, nameof(relativePath));
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        Kind = kind;
    }

    /// <summary>
    /// Gets the WorkspaceId value.
    /// </summary>
    public string WorkspaceId { get; }
    /// <summary>
    /// Gets the RelativePath value.
    /// </summary>
    public string RelativePath { get; }
    /// <summary>
    /// Gets the Kind value.
    /// </summary>
    public RequestedAuthorityScopeKind Kind { get; }

    internal static string RequireAuthorityPath(string? value, string parameterName)
    {
        if (value is null)
            throw new ArgumentNullException(parameterName);
        if (value.Length > 512 || value.Any(char.IsControl))
            throw new ArgumentException("Authority paths are bounded text without control characters.", parameterName);
        return value;
    }
}

/// <summary>
/// Desired authority for a delegation, expressed as data. This mirrors the
/// canonical structure Hufu hashes for derivation identity (action names,
/// scope triple, exclusions, validity bounds) so a host adapter can translate
/// it exactly, but Qingniao itself never evaluates containment, delegability,
/// or authorization against it.
/// </summary>
public sealed record RequestedAuthority
{
    private const int MaximumActions = 64;
    private const int MaximumExclusions = 128;

    /// <summary>
    /// Initializes a new instance of the RequestedAuthority type.
    /// </summary>
    public RequestedAuthority(
        IReadOnlyList<string> actions,
        RequestedAuthorityScope scope,
        IReadOnlyList<RequestedAuthorityScope> exclusions,
        DateTimeOffset notBefore,
        DateTimeOffset expiresAt)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(exclusions);
        if (actions.Count is < 1 or > MaximumActions)
            throw new ArgumentException("One or more bounded action names are required.", nameof(actions));
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in actions)
        {
            IdentityText.Require(action, nameof(actions), 128);
            if (!distinct.Add(action))
                throw new ArgumentException("Action names must be distinct.", nameof(actions));
        }

        if (exclusions.Count > MaximumExclusions)
            throw new ArgumentException("Too many exclusions.", nameof(exclusions));
        foreach (var exclusion in exclusions)
        {
            ArgumentNullException.ThrowIfNull(exclusion, nameof(exclusions));
        }

        if (notBefore >= expiresAt)
            throw new ArgumentException("Requested validity must be a non-empty interval.");
        Actions = actions.ToArray();
        Scope = scope;
        Exclusions = exclusions.ToArray();
        NotBefore = notBefore.ToUniversalTime();
        ExpiresAt = expiresAt.ToUniversalTime();
    }

    /// <summary>
    /// Gets the action names, for example <c>ReadFile</c>. Qingniao treats
    /// these as opaque labels; Hufu resolves them against its action set.
    /// </summary>
    public IReadOnlyList<string> Actions { get; }
    /// <summary>
    /// Gets the Scope value.
    /// </summary>
    public RequestedAuthorityScope Scope { get; }
    /// <summary>
    /// Gets the Exclusions value.
    /// </summary>
    public IReadOnlyList<RequestedAuthorityScope> Exclusions { get; }
    /// <summary>
    /// Gets the NotBefore value.
    /// </summary>
    public DateTimeOffset NotBefore { get; }
    /// <summary>
    /// Gets the ExpiresAt value.
    /// </summary>
    public DateTimeOffset ExpiresAt { get; }
}
