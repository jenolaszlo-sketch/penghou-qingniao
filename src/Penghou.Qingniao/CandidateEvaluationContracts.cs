namespace Penghou.Qingniao;

/// <summary>Immutable input to the deterministic candidate validator.</summary>
internal sealed record CandidateValidationRequest(
    CandidateRevisionReference Candidate,
    WorkerInvocationEvidence ImplementationInvocation,
    ExternalOperationCorrelation Correlation,
    string InvocationId);

/// <summary>Immutable input to the independent candidate reviewer.</summary>
internal sealed record CandidateReviewRequest(
    CandidateRevisionReference Candidate,
    WorkerInvocationEvidence ImplementationInvocation,
    ExternalOperationCorrelation Correlation,
    string InvocationId);

/// <summary>Immutable input to the one-shot semantic candidate corrector.</summary>
internal sealed record CandidateCorrectionRequest
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

    internal CandidateRevisionReference SourceCandidate { get; }
    internal ValidationEvidence? InitialValidation { get; }
    internal ReviewEvidence? InitialReview { get; }
    internal IReadOnlyList<EvidenceFinding> Findings { get; }
    internal NodeGenerationId TargetGeneration { get; }
    internal int TargetRevision { get; }
    internal string IdempotencyKey { get; }
    internal ExternalOperationCorrelation Correlation { get; }
    internal string InvocationId { get; }
    internal CandidateRevisionReference Candidate => SourceCandidate;
    internal ValidationEvidence? InitialValidationEvidence => InitialValidation;
    internal ReviewEvidence? InitialReviewEvidence => InitialReview;
    internal IReadOnlyList<EvidenceFinding> InitialFindings => Findings;
    internal NodeGenerationId TargetNodeGeneration => TargetGeneration;
    internal int TargetCandidateRevision => TargetRevision;
    internal string CorrectionIdempotencyKey => IdempotencyKey;
}

/// <summary>Provider-sealed output of one semantic correction attempt.</summary>
internal sealed record CandidateCorrectionOutcome(
    CandidateRevisionReference Candidate,
    WorkerInvocationEvidence ImplementationInvocation)
{
    internal CandidateRevisionReference CorrectedCandidate => Candidate;
    internal CandidateRevisionReference Corrected => Candidate;
    internal WorkerInvocationEvidence ImplementationEvidence => ImplementationInvocation;
}

/// <summary>Bounded semantic correction seam. Implementations must be idempotent.</summary>
internal interface ICandidateCorrector
{
    ValueTask<CandidateCorrectionOutcome> CorrectAsync(
        CandidateCorrectionRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Deterministic, authoritative M2.6 validation seam.</summary>
internal interface IDeterministicCandidateValidator
{
    ValueTask<ValidationEvidence> ValidateAsync(
        CandidateValidationRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Compatibility alias for hosts that call the validator a candidate validator.</summary>
internal interface ICandidateValidator : IDeterministicCandidateValidator
{
}

/// <summary>Independent, advisory M2.6 review seam.</summary>
internal interface IIndependentCandidateReviewer
{
    ValueTask<ReviewEvidence> ReviewAsync(
        CandidateReviewRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Compatibility alias for hosts that call the reviewer a candidate reviewer.</summary>
internal interface ICandidateReviewer : IIndependentCandidateReviewer
{
}

internal sealed record CandidateEvaluationOutcome(
    ValidationEvidence? Validation,
    ReviewEvidence? Review,
    Exception? ValidationFailure,
    Exception? ReviewFailure);
