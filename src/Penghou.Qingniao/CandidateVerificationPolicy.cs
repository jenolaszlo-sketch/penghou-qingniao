namespace Penghou.Qingniao;

/// <summary>Bounded supervision outcome for one candidate verification round.</summary>
public enum CandidateVerificationVerdict
{
    /// <summary>The candidate is accepted; supervision completes successfully.</summary>
    Accept = 0,

    /// <summary>The candidate is rejected; supervision completes as failed.</summary>
    Reject = 1,

    /// <summary>
    /// Supervision continues with an additional host constraint; the outcome
    /// is terminal and requires supervision.
    /// </summary>
    ContinueWithConstraint = 2,

    /// <summary>
    /// The candidate is re-executed locally within the same checkpoint;
    /// bounded by the policy round budget.
    /// </summary>
    RequestLocalReexecution = 3,
}

/// <summary>Immutable input to host candidate-verification policy.</summary>
public sealed record CandidateVerificationInput(
    ValidationEvidence? Validation,
    ReviewEvidence? Review,
    Exception? ValidationFailure,
    Exception? ReviewFailure,
    int Round)
{
    /// <summary>Validates this contract value and throws when an invariant is violated.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(Round, 1);
    }
}

/// <summary>Immutable host decision for one candidate verification round.</summary>
public sealed record CandidateVerificationDecision
{
    /// <summary>Initializes a verification decision.</summary>
    public CandidateVerificationDecision(
        CandidateVerificationVerdict verdict,
        string? constraint = null,
        string? note = null)
    {
        if (!Enum.IsDefined(verdict))
        {
            throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unknown candidate verification verdict.");
        }

        if ((verdict == CandidateVerificationVerdict.ContinueWithConstraint) != !string.IsNullOrWhiteSpace(constraint))
        {
            throw new ArgumentException(
                "A continue-with-constraint decision requires a constraint and no other verdict may carry one.",
                nameof(constraint));
        }

        if (note is not null && (string.IsNullOrWhiteSpace(note) || note.Length > 1_024))
        {
            throw new ArgumentException("A decision note must be non-empty and bounded.", nameof(note));
        }

        Verdict = verdict;
        Constraint = constraint;
        Note = note;
    }

    /// <summary>Gets the verdict.</summary>
    public CandidateVerificationVerdict Verdict { get; }

    /// <summary>Gets the additional host constraint, set only for continue-with-constraint.</summary>
    public string? Constraint { get; }

    /// <summary>Gets the optional bounded host note recorded with the outcome.</summary>
    public string? Note { get; }

    /// <summary>Creates an accept decision.</summary>
    public static CandidateVerificationDecision Accept(string? note = null) =>
        new(CandidateVerificationVerdict.Accept, null, note);

    /// <summary>Creates a reject decision.</summary>
    public static CandidateVerificationDecision Reject(string? note = null) =>
        new(CandidateVerificationVerdict.Reject, null, note);

    /// <summary>Creates a continue-with-constraint decision.</summary>
    public static CandidateVerificationDecision ContinueWithConstraint(string constraint, string? note = null) =>
        new(CandidateVerificationVerdict.ContinueWithConstraint, constraint, note);

    /// <summary>Creates a local-re-execution decision.</summary>
    public static CandidateVerificationDecision RequestLocalReexecution(string? note = null) =>
        new(CandidateVerificationVerdict.RequestLocalReexecution, null, note);
}

/// <summary>
/// Host candidate-verification policy. The policy interprets validation and
/// review evidence (pass criteria, review standards, repair budgets) and the
/// runtime executes the returned verdict. Product-specific evaluation policy
/// lives in host implementations of this seam, never in Qingniao core.
/// </summary>
public interface ICandidateVerificationPolicy
{
    /// <summary>Gets the maximum verification rounds, including re-executions. Must be at least one.</summary>
    int MaxVerificationRounds { get; }

    /// <summary>Decides one verification round outcome from its evidence.</summary>
    CandidateVerificationDecision Decide(CandidateVerificationInput input);
}

/// <summary>
/// Fail-closed verification policy used when a host configures evaluators
/// without supplying a policy: nothing is accepted without host judgment.
/// </summary>
internal sealed class FailClosedVerificationPolicy : ICandidateVerificationPolicy
{
    internal static readonly FailClosedVerificationPolicy Instance = new();

    /// <summary>Gets the single verification round; no re-execution is permitted.</summary>
    public int MaxVerificationRounds => 1;

    /// <summary>Rejects every verification round.</summary>
    public CandidateVerificationDecision Decide(CandidateVerificationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return CandidateVerificationDecision.Reject("No host verification policy is configured.");
    }

    private FailClosedVerificationPolicy()
    {
    }
}
