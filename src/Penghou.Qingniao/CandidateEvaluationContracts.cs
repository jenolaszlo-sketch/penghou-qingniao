namespace Penghou.Qingniao;

/// <summary>Immutable input to the deterministic candidate validator.</summary>
public sealed record CandidateValidationRequest(
    CandidateRevisionReference Candidate,
    WorkerInvocationEvidence ImplementationInvocation,
    ExternalOperationCorrelation Correlation,
    string InvocationId);

/// <summary>Immutable input to the independent candidate reviewer.</summary>
public sealed record CandidateReviewRequest(
    CandidateRevisionReference Candidate,
    WorkerInvocationEvidence ImplementationInvocation,
    ExternalOperationCorrelation Correlation,
    string InvocationId);

/// <summary>Immutable input to the one-shot semantic candidate corrector.</summary>
public sealed record CandidateCorrectionRequest
{
    internal CandidateCorrectionRequest(
        CandidateRevisionReference sourceCandidate,
        ValidationEvidence? initialValidation,
        ReviewEvidence? initialReview,
        IReadOnlyList<EvidenceFinding> findings,
        NodeGenerationId targetGeneration,
        int targetRevision,
        string idempotencyKey,
        ExternalOperationCorrelation correlation,
        string invocationId)
    {
        SourceCandidate = sourceCandidate ?? throw new ArgumentNullException(nameof(sourceCandidate));
        InitialValidation = initialValidation;
        InitialReview = initialReview;
        ArgumentNullException.ThrowIfNull(findings);
        if (findings.Count > MaximumFindings)
        {
            throw new ArgumentException($"A correction request cannot contain more than {MaximumFindings} findings.", nameof(findings));
        }

        Findings = Array.AsReadOnly(findings.ToArray());
        targetGeneration.Validate();
        TargetGeneration = targetGeneration;
        if (targetRevision < 1) throw new ArgumentOutOfRangeException(nameof(targetRevision));
        TargetRevision = targetRevision;
        IdempotencyKey = RequireIdentity(idempotencyKey, nameof(idempotencyKey));
        Correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
        Correlation.Validate();
        InvocationId = RequireIdentity(invocationId, nameof(invocationId));
    }

    private const int MaximumFindings = 128;

    private static string RequireIdentity(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512)
        {
            throw new ArgumentException("A bounded correction identity is required.", parameterName);
        }

        return value;
    }

    /// <summary>Gets the candidate revision to correct.</summary>
    public CandidateRevisionReference SourceCandidate { get; }
    /// <summary>Gets the initial validation evidence, if any.</summary>
    public ValidationEvidence? InitialValidation { get; }
    /// <summary>Gets the initial review evidence, if any.</summary>
    public ReviewEvidence? InitialReview { get; }
    /// <summary>Gets the validation and review findings to address.</summary>
    public IReadOnlyList<EvidenceFinding> Findings { get; }
    /// <summary>Gets the new node generation the correction must use.</summary>
    public NodeGenerationId TargetGeneration { get; }
    /// <summary>Gets the candidate revision the correction must produce.</summary>
    public int TargetRevision { get; }
    /// <summary>Gets the correction idempotency key.</summary>
    public string IdempotencyKey { get; }
    /// <summary>Gets the execution correlation for the correction attempt.</summary>
    public ExternalOperationCorrelation Correlation { get; }
    /// <summary>Gets the correction invocation identity.</summary>
    public string InvocationId { get; }
    /// <summary>Gets the source candidate (compatibility alias).</summary>
    public CandidateRevisionReference Candidate => SourceCandidate;
    /// <summary>Gets the initial validation evidence (compatibility alias).</summary>
    public ValidationEvidence? InitialValidationEvidence => InitialValidation;
    /// <summary>Gets the initial review evidence (compatibility alias).</summary>
    public ReviewEvidence? InitialReviewEvidence => InitialReview;
    /// <summary>Gets the correction findings (compatibility alias).</summary>
    public IReadOnlyList<EvidenceFinding> InitialFindings => Findings;
    /// <summary>Gets the target generation (compatibility alias).</summary>
    public NodeGenerationId TargetNodeGeneration => TargetGeneration;
    /// <summary>Gets the target revision (compatibility alias).</summary>
    public int TargetCandidateRevision => TargetRevision;
    /// <summary>Gets the correction idempotency key (compatibility alias).</summary>
    public string CorrectionIdempotencyKey => IdempotencyKey;
}

/// <summary>Provider-sealed output of one semantic correction attempt.</summary>
public sealed record CandidateCorrectionOutcome(
    CandidateRevisionReference Candidate,
    WorkerInvocationEvidence ImplementationInvocation)
{
    /// <summary>Gets the corrected candidate.</summary>
    public CandidateRevisionReference CorrectedCandidate => Candidate;
    /// <summary>Gets the corrected candidate (compatibility alias).</summary>
    public CandidateRevisionReference Corrected => Candidate;
    /// <summary>Gets the correction implementation evidence (compatibility alias).</summary>
    public WorkerInvocationEvidence ImplementationEvidence => ImplementationInvocation;
}

/// <summary>Bounded semantic correction seam. Implementations must be idempotent.</summary>
public interface ICandidateCorrector
{
    /// <summary>Produces one corrected candidate revision from the request findings.</summary>
    ValueTask<CandidateCorrectionOutcome> CorrectAsync(
        CandidateCorrectionRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Deterministic, authoritative validation seam.</summary>
public interface IDeterministicCandidateValidator
{
    /// <summary>Validates one sealed candidate revision deterministically.</summary>
    ValueTask<ValidationEvidence> ValidateAsync(
        CandidateValidationRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Compatibility alias for hosts that call the validator a candidate validator.</summary>
public interface ICandidateValidator : IDeterministicCandidateValidator
{
}

/// <summary>Independent, advisory review seam.</summary>
public interface IIndependentCandidateReviewer
{
    /// <summary>Independently reviews one sealed candidate revision.</summary>
    ValueTask<ReviewEvidence> ReviewAsync(
        CandidateReviewRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Compatibility alias for hosts that call the reviewer a candidate reviewer.</summary>
public interface ICandidateReviewer : IIndependentCandidateReviewer
{
}

internal sealed record CandidateEvaluationOutcome(
    ValidationEvidence? Validation,
    ReviewEvidence? Review,
    Exception? ValidationFailure,
    Exception? ReviewFailure);
