namespace Penghou.Qingniao;

/// <summary>
/// Outcome of the host-owned pre-execution authority preflight for one
/// delegation generation. This deliberately does not reuse
/// <see cref="DelegationAdmissionStatus"/>: it preserves the host distinction
/// between an explicit refusal and an authority provider that could not be
/// reached.
/// </summary>
public enum DelegationAuthorityPreflightStatus
{
    /// <summary>The generation may start, carrying its execution attachment.</summary>
    Permit = 0,

    /// <summary>The authority provider explicitly refused the request.</summary>
    Deny = 1,

    /// <summary>The authority provider could not be reached or gave no decision.</summary>
    Unavailable = 2,
}

/// <summary>
/// Exact Qingniao-owned execution identity plus host-supplied labels presented
/// to the host authority preflight. Qingniao supplies the values unchanged and
/// never interprets <see cref="ParentGrantId"/> or <see cref="RequestedAuthority"/>.
/// </summary>
public sealed class DelegationAuthorityPreflightContext
{
    /// <summary>
    /// Initializes a new instance of the DelegationAuthorityPreflightContext type.
    /// </summary>
    public DelegationAuthorityPreflightContext(
        DelegationId delegationId,
        NodeGenerationId generation,
        string parentGrantId,
        RequestedAuthority requestedAuthority,
        SupervisorIdentity? supervisor = null)
    {
        generation.Validate();
        DelegationId = delegationId;
        Generation = generation;
        if (string.IsNullOrEmpty(parentGrantId) || parentGrantId.Length > 256)
        {
            throw new ArgumentException("A parent grant identifier must be non-empty and bounded.", nameof(parentGrantId));
        }

        ParentGrantId = parentGrantId;
        RequestedAuthority = requestedAuthority ?? throw new ArgumentNullException(nameof(requestedAuthority));
        Supervisor = supervisor;
    }

    /// <summary>Gets the delegation identity.</summary>
    public DelegationId DelegationId { get; }

    /// <summary>Gets the generation the preflight applies to.</summary>
    public NodeGenerationId Generation { get; }

    /// <summary>Gets the opaque parent grant label. A label only: Qingniao never evaluates it.</summary>
    public string ParentGrantId { get; }

    /// <summary>Gets the requested authority as data. Qingniao never evaluates it.</summary>
    public RequestedAuthority RequestedAuthority { get; }

    /// <summary>
    /// Gets the optional host-supplied supervisor label, present when the
    /// generation was created by a supervised intervention. A label only: this
    /// is never an authentication credential.
    /// </summary>
    public SupervisorIdentity? Supervisor { get; }
}

/// <summary>Immutable host decision produced by the authority preflight.</summary>
public sealed record DelegationAuthorityPreflightDecision
{
    private const int MaximumReasonLength = 2_048;

    private DelegationAuthorityPreflightDecision(
        DelegationAuthorityPreflightStatus status,
        DelegationExecutionAttachment? executionAttachment,
        string? reason)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown authority preflight status.");
        }

        if (status == DelegationAuthorityPreflightStatus.Permit)
        {
            if (executionAttachment is null)
            {
                throw new ArgumentException("A permit requires an execution attachment.", nameof(executionAttachment));
            }

            if (reason is not null)
            {
                throw new ArgumentException("A permit carries no reason.", nameof(reason));
            }
        }
        else
        {
            if (executionAttachment is not null)
            {
                throw new ArgumentException("Only a permit carries an execution attachment.", nameof(executionAttachment));
            }

            if (string.IsNullOrWhiteSpace(reason))
            {
                throw new ArgumentException("A non-permit decision requires a reason.", nameof(reason));
            }
        }

        Status = status;
        ExecutionAttachment = executionAttachment;
        Reason = reason is null ? null : RequireReason(reason);
    }

    /// <summary>Gets the preflight status.</summary>
    public DelegationAuthorityPreflightStatus Status { get; }

    /// <summary>Gets the opaque execution attachment, or <see langword="null"/> when not permitted.</summary>
    public DelegationExecutionAttachment? ExecutionAttachment { get; }

    /// <summary>Gets the host-stated reason, or <see langword="null"/> when permitted.</summary>
    public string? Reason { get; }

    /// <summary>Gets whether the generation is permitted to start.</summary>
    public bool IsPermit => Status == DelegationAuthorityPreflightStatus.Permit;

    /// <summary>Creates a permit carrying the host execution attachment.</summary>
    public static DelegationAuthorityPreflightDecision Permit(DelegationExecutionAttachment executionAttachment) =>
        new(DelegationAuthorityPreflightStatus.Permit, executionAttachment, null);

    /// <summary>Creates an explicit refusal with a host-stated reason.</summary>
    public static DelegationAuthorityPreflightDecision Deny(string reason) =>
        new(DelegationAuthorityPreflightStatus.Deny, null, reason);

    /// <summary>Creates an unavailable outcome with a host-stated reason.</summary>
    public static DelegationAuthorityPreflightDecision Unavailable(string reason) =>
        new(DelegationAuthorityPreflightStatus.Unavailable, null, reason);

    private static string RequireReason(string value)
    {
        var normalized = value.Normalize(System.Text.NormalizationForm.FormC)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
        if (normalized.Length == 0 || normalized.Length > MaximumReasonLength)
        {
            throw new ArgumentException("A preflight reason must be non-empty and bounded.", nameof(value));
        }

        return normalized;
    }
}

/// <summary>
/// Host policy seam for the pre-execution authority preflight. Qingniao passes
/// the exact execution identity and authority labels through unchanged and
/// carries the returned attachment opaquely; it never interprets authority.
/// </summary>
public interface IDelegationAuthorityPreflight
{
    /// <summary>
    /// Permits, denies, or reports unavailable for one delegation generation.
    /// Invoked after the generation identity exists and before any durable
    /// startable record is committed.
    /// </summary>
    ValueTask<DelegationAuthorityPreflightDecision> PreflightAsync(
        DelegationAuthorityPreflightContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Raised when the host authority preflight denies a delegation or the
/// authority provider is unavailable. The delegation must not start; neither
/// outcome degrades to parent authority, no authority, default admit, or
/// provider capability.
/// </summary>
public sealed class DelegationAuthorityPreflightException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the DelegationAuthorityPreflightException type.
    /// </summary>
    public DelegationAuthorityPreflightException(DelegationAuthorityPreflightStatus status, string reason)
        : base(reason)
    {
        if (!Enum.IsDefined(status) || status == DelegationAuthorityPreflightStatus.Permit)
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "A preflight exception must carry a non-permit status.");
        }

        Status = status;
        Reason = reason;
    }

    /// <summary>Gets the distinct non-permit status.</summary>
    public DelegationAuthorityPreflightStatus Status { get; }

    /// <summary>Gets the host-stated reason.</summary>
    public string Reason { get; }
}
