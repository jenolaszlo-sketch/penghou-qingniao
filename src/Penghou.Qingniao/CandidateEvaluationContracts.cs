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
