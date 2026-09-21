namespace Penghou.Qingniao;

/// <summary>Identifies the outcome of delegation admission verification.</summary>
public enum DelegationAdmissionStatus
{
    /// <summary>The delegation is admitted for acceptance and execution.</summary>
    Admitted = 0,

    /// <summary>The verifier does not know the delegation fence or caller.</summary>
    Unknown = 1,

    /// <summary>The caller is not authorized for the requested delegation.</summary>
    Unauthorized = 2,

    /// <summary>The delegation was rejected for a host-stated reason.</summary>
    Rejected = 3,
}

/// <summary>Immutable host decision admitting or rejecting one delegation.</summary>
public sealed record DelegationAdmissionDecision
{
    /// <summary>Initializes an admission decision.</summary>
    public DelegationAdmissionDecision(DelegationAdmissionStatus status, string? reason = null)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown delegation admission status.");
        }

        if (status == DelegationAdmissionStatus.Admitted && reason is not null)
        {
            throw new ArgumentException("An admitted decision carries no reason.", nameof(reason));
        }

        if (status != DelegationAdmissionStatus.Admitted && string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A non-admitted decision requires a reason.", nameof(reason));
        }

        Status = status;
        Reason = reason is null ? null : RequireReason(reason, nameof(reason));
    }

    /// <summary>Gets the admission status.</summary>
    public DelegationAdmissionStatus Status { get; }

    /// <summary>Gets the host-stated reason, or <see langword="null"/> when admitted.</summary>
    public string? Reason { get; }

    /// <summary>Gets whether the delegation is admitted.</summary>
    public bool IsAdmitted => Status == DelegationAdmissionStatus.Admitted;

    /// <summary>Creates an admitted decision.</summary>
    public static DelegationAdmissionDecision Admitted() =>
        new(DelegationAdmissionStatus.Admitted);

    /// <summary>Creates a rejected decision with a host-stated reason.</summary>
    public static DelegationAdmissionDecision Reject(DelegationAdmissionStatus status, string reason)
    {
        if (status == DelegationAdmissionStatus.Admitted)
        {
            throw new ArgumentException("An admitted status must use Admitted().", nameof(status));
        }

        return new DelegationAdmissionDecision(status, reason);
    }

    private static string RequireReason(string value, string parameterName)
    {
        var normalized = value.Normalize(System.Text.NormalizationForm.FormC)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
        if (normalized.Length == 0 || normalized.Length > 2_048)
        {
            throw new ArgumentException("An admission reason must be non-empty and bounded.", parameterName);
        }

        return normalized;
    }
}

/// <summary>Exact caller and request supplied to host admission policy.</summary>
public sealed class DelegationAdmissionContext
{
    /// <summary>Initializes an admission context.</summary>
    public DelegationAdmissionContext(DelegationCallerScope caller, DelegationRequest request)
    {
        Caller = caller ?? throw new ArgumentNullException(nameof(caller));
        Request = request ?? throw new ArgumentNullException(nameof(request));
        DelegationRequestValidator.Validate(request);
    }

    /// <summary>Gets the authenticated caller scope.</summary>
    public DelegationCallerScope Caller { get; }

    /// <summary>Gets the delegation request awaiting admission.</summary>
    public DelegationRequest Request { get; }
}

/// <summary>
/// Host policy seam for delegation admission. Qingniao passes the caller and
/// request through unchanged; the host decides whether the delegation,
/// including any opaque external fence it carries, is admitted. Qingniao
/// never interprets what a fence represents.
/// </summary>
public interface IDelegationAdmissionVerifier
{
    /// <summary>Admits or rejects the exact caller and request.</summary>
    DelegationAdmissionDecision Verify(DelegationAdmissionContext context);
}

/// <summary>Raised when host admission policy rejects a delegation.</summary>
public sealed class DelegationAdmissionException : InvalidOperationException
{
    /// <summary>Initializes an admission failure.</summary>
    public DelegationAdmissionException(
        string message,
        DelegationAdmissionStatus status,
        string? reason = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        if (!Enum.IsDefined(status) || status == DelegationAdmissionStatus.Admitted)
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "An admission failure requires a non-admitted status.");
        }

        Status = status;
        Reason = reason;
    }

    /// <summary>Gets the normalized rejection status.</summary>
    public DelegationAdmissionStatus Status { get; }

    /// <summary>Gets the host-stated reason, if any.</summary>
    public string? Reason { get; }
}
