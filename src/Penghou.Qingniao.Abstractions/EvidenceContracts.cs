namespace Penghou.Qingniao;

/// <summary>Stable names for the evidence categories defined by Qingniao.</summary>
public static class EvidenceKinds
{
    /// <summary>
    /// Provides the AgentExecution contract constant.
    /// </summary>
    public const string AgentExecution = "agent.execution";
    /// <summary>
    /// Provides the ModelExecution contract constant.
    /// </summary>
    public const string ModelExecution = "model.execution";
    /// <summary>
    /// Provides the DeterministicExecution contract constant.
    /// </summary>
    public const string DeterministicExecution = "deterministic.execution";
    /// <summary>
    /// Provides the Validation contract constant.
    /// </summary>
    public const string Validation = "validation.report";
    /// <summary>
    /// Provides the Review contract constant.
    /// </summary>
    public const string Review = "review.report";
}

/// <summary>
/// An immutable, normalized receipt for one worker invocation. The category,
/// disposition, and capability names are open strings so adapters can add
/// provider-specific semantics without changing the Qingniao package. Provider
/// details that are needed for audit but have no common meaning belong in
/// <see cref="ProviderData"/> or, preferably, an artifact reference.
/// </summary>
public sealed record WorkerInvocationEvidence
{
    /// <summary>
    /// Initializes a new instance of the WorkerInvocationEvidence type.
    /// </summary>
    public WorkerInvocationEvidence(
        DelegationId delegationId,
        StructuralNodeReference structuralNode,
        NodeGenerationId nodeGeneration,
        string executionCategory,
        ProviderExecutionAttemptReference attempt,
        string disposition,
        DateTimeOffset startedAt,
        DateTimeOffset? completedAt,
        string? capability,
        string? profile,
        string? requestedProvider,
        string? requestedModel,
        string? resolvedModel,
        IReadOnlyList<string> toolCapabilities,
        IReadOnlyList<DelegationArtifactReference> inputArtifacts,
        IReadOnlyList<DelegationArtifactReference> outputArtifacts,
        CandidateRevisionReference? candidate = null,
        IReadOnlyDictionary<string, string>? usage = null,
        IReadOnlyDictionary<string, string>? providerData = null,
        ExternalOperationCorrelation? executionCorrelation = null)
    {
        ArtifactContracts.RequireDelegation(delegationId, nameof(delegationId));
        structuralNode.Validate();
        nodeGeneration.Validate();
        ExecutionCategory = EvidenceContracts.Name(executionCategory, nameof(executionCategory));
        attempt = attempt ?? throw new ArgumentNullException(nameof(attempt));
        attempt.Validate();
        Disposition = EvidenceContracts.Name(disposition, nameof(disposition));
        if (startedAt == default)
        {
            throw new ArgumentException("A worker invocation must have a start time.", nameof(startedAt));
        }

        if (completedAt is not null && completedAt < startedAt)
        {
            throw new ArgumentException("An invocation cannot complete before it starts.", nameof(completedAt));
        }

        DelegationId = delegationId;
        StructuralNode = structuralNode;
        NodeGeneration = nodeGeneration;
        Attempt = attempt;
        StartedAt = startedAt;
        CompletedAt = completedAt;
        Capability = EvidenceContracts.OptionalIdentifier(capability, nameof(capability));
        Profile = EvidenceContracts.OptionalIdentifier(profile, nameof(profile));
        RequestedProvider = EvidenceContracts.OptionalIdentifier(requestedProvider, nameof(requestedProvider));
        RequestedModel = EvidenceContracts.OptionalIdentifier(requestedModel, nameof(requestedModel));
        ResolvedModel = EvidenceContracts.OptionalIdentifier(resolvedModel, nameof(resolvedModel));
        ToolCapabilities = EvidenceContracts.Names(toolCapabilities, nameof(toolCapabilities));
        InputArtifacts = EvidenceContracts.Artifacts(inputArtifacts, delegationId, nameof(inputArtifacts));
        OutputArtifacts = EvidenceContracts.Artifacts(
            outputArtifacts,
            delegationId,
            nameof(outputArtifacts),
            structuralNode,
            nodeGeneration);
        if (executionCorrelation is not null)
        {
            executionCorrelation.Validate();
            if (executionCorrelation.DelegationId != delegationId
                || executionCorrelation.StructuralNode != structuralNode
                || executionCorrelation.NodeGeneration != nodeGeneration
                || !string.Equals(executionCorrelation.ExecutionAttemptId, attempt.AttemptId, StringComparison.Ordinal)
                || !string.Equals(executionCorrelation.Agent.Provider, attempt.Provider, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Execution correlation must exactly identify the invocation owner and provider attempt.",
                    nameof(executionCorrelation));
            }
        }
        if (candidate is not null)
        {
            if (candidate.DelegationId != delegationId
                || candidate.StructuralNode != structuralNode
                || candidate.NodeGeneration != nodeGeneration)
            {
                throw new ArgumentException(
                    "A candidate reference must belong to the invocation delegation, node, and generation.",
                    nameof(candidate));
            }
        }

        Candidate = candidate;
        ExecutionCorrelation = executionCorrelation;
        Usage = EvidenceContracts.Properties(usage, nameof(usage));
        ProviderData = EvidenceContracts.Properties(providerData, nameof(providerData));
    }

    /// <summary>
    /// Gets the DelegationId value.
    /// </summary>
    public DelegationId DelegationId { get; }
    /// <summary>
    /// Gets the StructuralNode value.
    /// </summary>
    public StructuralNodeReference StructuralNode { get; }
    /// <summary>
    /// Gets the NodeGeneration value.
    /// </summary>
    public NodeGenerationId NodeGeneration { get; }
    /// <summary>
    /// Gets the ExecutionCategory value.
    /// </summary>
    public string ExecutionCategory { get; }
    /// <summary>
    /// Gets the Attempt value.
    /// </summary>
    public ProviderExecutionAttemptReference Attempt { get; }
    /// <summary>
    /// Gets the Disposition value.
    /// </summary>
    public string Disposition { get; }
    /// <summary>
    /// Gets the StartedAt value.
    /// </summary>
    public DateTimeOffset StartedAt { get; }
    /// <summary>
    /// Gets the CompletedAt value.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; }
    /// <summary>
    /// Gets the Capability value.
    /// </summary>
    public string? Capability { get; }
    /// <summary>
    /// Gets the Profile value.
    /// </summary>
    public string? Profile { get; }
    /// <summary>
    /// Gets the RequestedProvider value.
    /// </summary>
    public string? RequestedProvider { get; }
    /// <summary>
    /// Gets the RequestedModel value.
    /// </summary>
    public string? RequestedModel { get; }
    /// <summary>
    /// Gets the ResolvedModel value.
    /// </summary>
    public string? ResolvedModel { get; }
    /// <summary>
    /// Gets the ToolCapabilities value.
    /// </summary>
    public IReadOnlyList<string> ToolCapabilities { get; }
    /// <summary>
    /// Gets the InputArtifacts value.
    /// </summary>
    public IReadOnlyList<DelegationArtifactReference> InputArtifacts { get; }
    /// <summary>
    /// Gets the OutputArtifacts value.
    /// </summary>
    public IReadOnlyList<DelegationArtifactReference> OutputArtifacts { get; }
    /// <summary>
    /// Gets the Candidate value.
    /// </summary>
    public CandidateRevisionReference? Candidate { get; }
    /// <summary>
    /// Optional exact external-operation correlation. When present, the
    /// provider attempt cannot be combined with another invocation owner.
    /// </summary>
    public ExternalOperationCorrelation? ExecutionCorrelation { get; }
    /// <summary>
    /// Provider-reported usage measurements. Values are informational and must
    /// not contain credentials, secrets, or large payloads.
    /// </summary>
    public IReadOnlyDictionary<string, string> Usage { get; }
    /// <summary>
    /// Bounded provider-specific metadata. Credentials, access tokens, secret
    /// material, transcripts, and large payloads are forbidden; publish those
    /// through a retained-policy artifact and reference it instead.
    /// </summary>
    public IReadOnlyDictionary<string, string> ProviderData { get; }
}

/// <summary>A finding emitted by a deterministic validator or reviewer.</summary>
public sealed record EvidenceFinding
{
    /// <summary>
    /// Initializes a new instance of the EvidenceFinding type.
    /// </summary>
    public EvidenceFinding(
        string code,
        string severity,
        string summary,
        bool resolved,
        IReadOnlyDictionary<string, string>? details = null)
    {
        Code = EvidenceContracts.Name(code, nameof(code));
        Severity = EvidenceContracts.Name(severity, nameof(severity));
        Summary = IdentityText.RequireProse(summary, nameof(summary), EvidenceContracts.MaximumSummaryLength);
        Resolved = resolved;
        Details = EvidenceContracts.Properties(details, nameof(details));
    }

    /// <summary>
    /// Gets the Code value.
    /// </summary>
    public string Code { get; }
    /// <summary>
    /// Gets the Severity value.
    /// </summary>
    public string Severity { get; }
    /// <summary>
    /// Gets the Summary value.
    /// </summary>
    public string Summary { get; }
    /// <summary>
    /// Gets the Resolved value.
    /// </summary>
    public bool Resolved { get; }
    /// <summary>
    /// Gets the Details value.
    /// </summary>
    public IReadOnlyDictionary<string, string> Details { get; }
}

/// <summary>Normalized deterministic validation evidence for a candidate.</summary>
public sealed record ValidationEvidence
{
    /// <summary>
    /// Initializes a new instance of the ValidationEvidence type.
    /// </summary>
    public ValidationEvidence(
        WorkerInvocationEvidence invocation,
        string outcome,
        IReadOnlyList<EvidenceFinding> findings,
        string? validator = null,
        CandidateRevisionReference? candidate = null)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (!invocation.ExecutionCategory.StartsWith("deterministic.", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Validation evidence must reference a deterministic execution invocation.",
                nameof(invocation));
        }

        Invocation = invocation;
        Outcome = EvidenceContracts.Name(outcome, nameof(outcome));
        Validator = EvidenceContracts.OptionalIdentifier(validator, nameof(validator));
        Findings = EvidenceContracts.Findings(findings, nameof(findings));
        Candidate = candidate ?? invocation.Candidate;
        EvidenceContracts.ValidateCandidate(Candidate, invocation, nameof(candidate));
    }

    /// <summary>
    /// Gets the Invocation value.
    /// </summary>
    public WorkerInvocationEvidence Invocation { get; }
    /// <summary>
    /// Gets the Outcome value.
    /// </summary>
    public string Outcome { get; }
    /// <summary>
    /// Gets the Validator value.
    /// </summary>
    public string? Validator { get; }
    /// <summary>
    /// Gets the Findings value.
    /// </summary>
    public IReadOnlyList<EvidenceFinding> Findings { get; }
    /// <summary>
    /// Gets the Candidate value.
    /// </summary>
    public CandidateRevisionReference? Candidate { get; }
}

/// <summary>The evidence value of one comparison dimension for a review.</summary>
public enum IndependenceAssessment
{
    /// <summary>
    /// Identifies the Same enum value.
    /// </summary>
    Same = 0,
    /// <summary>
    /// Identifies the Different enum value.
    /// </summary>
    Different = 1,
    /// <summary>
    /// Identifies the Unknown enum value.
    /// </summary>
    Unknown = 2,
    /// <summary>
    /// Identifies the NotApplicable enum value.
    /// </summary>
    NotApplicable = 3,
}

/// <summary>
/// Records the observable dimensions of review independence. It is evidence,
/// not an authorization decision: policy decides which dimensions are required.
/// </summary>
public sealed record ReviewIndependenceEvidence
{
    /// <summary>
    /// Initializes a new instance of the ReviewIndependenceEvidence type.
    /// </summary>
    public ReviewIndependenceEvidence(
        string implementationInvocationId,
        string reviewInvocationId,
        IndependenceAssessment invocation,
        IndependenceAssessment context,
        IndependenceAssessment profile,
        IndependenceAssessment model,
        IndependenceAssessment provider,
        IReadOnlyDictionary<string, string>? details = null)
    {
        ImplementationInvocationId = EvidenceContracts.Identifier(implementationInvocationId, nameof(implementationInvocationId));
        ReviewInvocationId = EvidenceContracts.Identifier(reviewInvocationId, nameof(reviewInvocationId));
        Invocation = ValidateAssessment(invocation, nameof(invocation));
        Context = ValidateAssessment(context, nameof(context));
        Profile = ValidateAssessment(profile, nameof(profile));
        Model = ValidateAssessment(model, nameof(model));
        Provider = ValidateAssessment(provider, nameof(provider));
        Details = EvidenceContracts.Properties(details, nameof(details));

        if (string.Equals(ImplementationInvocationId, ReviewInvocationId, StringComparison.Ordinal)
            && Invocation == IndependenceAssessment.Different)
        {
            throw new ArgumentException(
                "The invocation dimension cannot be different when both invocation identities are equal.",
                nameof(invocation));
        }

        if (!string.Equals(ImplementationInvocationId, ReviewInvocationId, StringComparison.Ordinal)
            && Invocation == IndependenceAssessment.Same)
        {
            throw new ArgumentException(
                "The invocation dimension cannot be same when invocation identities differ.",
                nameof(invocation));
        }
    }

    /// <summary>
    /// Gets the ImplementationInvocationId value.
    /// </summary>
    public string ImplementationInvocationId { get; }
    /// <summary>
    /// Gets the ReviewInvocationId value.
    /// </summary>
    public string ReviewInvocationId { get; }
    /// <summary>
    /// Gets the Invocation value.
    /// </summary>
    public IndependenceAssessment Invocation { get; }
    /// <summary>
    /// Gets the Context value.
    /// </summary>
    public IndependenceAssessment Context { get; }
    /// <summary>
    /// Gets the Profile value.
    /// </summary>
    public IndependenceAssessment Profile { get; }
    /// <summary>
    /// Gets the Model value.
    /// </summary>
    public IndependenceAssessment Model { get; }
    /// <summary>
    /// Gets the Provider value.
    /// </summary>
    public IndependenceAssessment Provider { get; }
    /// <summary>
    /// Gets the Details value.
    /// </summary>
    public IReadOnlyDictionary<string, string> Details { get; }

    private static IndependenceAssessment ValidateAssessment(IndependenceAssessment value, string parameterName)
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Unknown independence assessment.");
        }

        return value;
    }
}

/// <summary>Normalized review evidence associated with a worker invocation.</summary>
public sealed record ReviewEvidence
{
    /// <summary>
    /// Initializes a new instance of the ReviewEvidence type.
    /// </summary>
    public ReviewEvidence(
        WorkerInvocationEvidence invocation,
        string outcome,
        IReadOnlyList<EvidenceFinding> findings,
        ReviewIndependenceEvidence independence,
        CandidateRevisionReference candidate,
        string reviewer)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (!invocation.ExecutionCategory.StartsWith("agent.", StringComparison.Ordinal)
            && !invocation.ExecutionCategory.StartsWith("model.", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Review evidence must reference an agent or model invocation.",
                nameof(invocation));
        }

        ArgumentNullException.ThrowIfNull(independence);
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.DelegationId != invocation.DelegationId
            || candidate.StructuralNode != invocation.StructuralNode
            || candidate.NodeGeneration != invocation.NodeGeneration)
        {
            throw new ArgumentException(
                "A review candidate must belong to the invocation delegation, node, and generation.",
                nameof(candidate));
        }
        if (invocation.Candidate is not null
            && !CandidateRevisionIdentity.SubjectEqual(candidate, invocation.Candidate))
        {
            throw new ArgumentException(
                "A review candidate must exactly match the invocation candidate reference.",
                nameof(candidate));
        }
        if (!string.Equals(independence.ReviewInvocationId, invocation.Attempt.AttemptId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Review independence must identify the review invocation being reported.",
                nameof(independence));
        }

        Invocation = invocation;
        Outcome = EvidenceContracts.Name(outcome, nameof(outcome));
        Findings = EvidenceContracts.Findings(findings, nameof(findings));
        Independence = independence;
        Candidate = candidate;
        Reviewer = EvidenceContracts.Identifier(reviewer, nameof(reviewer));
    }

    /// <summary>
    /// Gets the Invocation value.
    /// </summary>
    public WorkerInvocationEvidence Invocation { get; }
    /// <summary>
    /// Gets the Outcome value.
    /// </summary>
    public string Outcome { get; }
    /// <summary>
    /// Gets the Findings value.
    /// </summary>
    public IReadOnlyList<EvidenceFinding> Findings { get; }
    /// <summary>
    /// Gets the Independence value.
    /// </summary>
    public ReviewIndependenceEvidence Independence { get; }
    /// <summary>
    /// Gets the Candidate value.
    /// </summary>
    public CandidateRevisionReference Candidate { get; }
    /// <summary>
    /// Gets the Reviewer value.
    /// </summary>
    public string Reviewer { get; }
}

/// <summary>
/// A bounded immutable set of normalized evidence attached to a publication.
/// The records contain references and bounded metadata only; transcripts and
/// provider payloads remain separately published artifacts.
/// </summary>
public sealed record EvidenceBundle
{
    /// <summary>
    /// Initializes a new instance of the EvidenceBundle type.
    /// </summary>
    public EvidenceBundle(
        IReadOnlyList<WorkerInvocationEvidence>? invocations = null,
        IReadOnlyList<ValidationEvidence>? validations = null,
        IReadOnlyList<ReviewEvidence>? reviews = null)
    {
        Invocations = EvidenceContracts.Evidence(invocations, nameof(invocations));
        Validations = EvidenceContracts.Evidence(validations, nameof(validations));
        Reviews = EvidenceContracts.Evidence(reviews, nameof(reviews));

        EvidenceContracts.RequireUniqueInvocations(Invocations, nameof(invocations));
        EvidenceContracts.RequireUniqueValidations(Validations, nameof(validations));
        EvidenceContracts.RequireUniqueReviews(Reviews, nameof(reviews));
    }

    /// <summary>
    /// Gets the Invocations value.
    /// </summary>
    public IReadOnlyList<WorkerInvocationEvidence> Invocations { get; }
    /// <summary>
    /// Gets the Validations value.
    /// </summary>
    public IReadOnlyList<ValidationEvidence> Validations { get; }
    /// <summary>
    /// Gets the Reviews value.
    /// </summary>
    public IReadOnlyList<ReviewEvidence> Reviews { get; }
}

/// <summary>Semantic identity comparison for immutable evidence publications.</summary>
public static class EvidenceBundleIdentity
{
    /// <summary>
    /// Performs the SemanticallyEqual contract operation.
    /// </summary>
    public static bool SemanticallyEqual(EvidenceBundle? left, EvidenceBundle? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        return left.Invocations.SequenceEqual(right.Invocations, EvidenceContracts.InvocationComparer)
            && left.Validations.SequenceEqual(right.Validations, EvidenceContracts.ValidationComparer)
            && left.Reviews.SequenceEqual(right.Reviews, EvidenceContracts.ReviewComparer);
    }
}

internal static class EvidenceContracts
{
    /// <summary>
    /// Provides the MaximumArtifacts contract constant.
    /// </summary>
    public const int MaximumArtifacts = 128;
    /// <summary>
    /// Provides the MaximumFindings contract constant.
    /// </summary>
    public const int MaximumFindings = 128;
    /// <summary>
    /// Provides the MaximumProperties contract constant.
    /// </summary>
    public const int MaximumProperties = 64;
    /// <summary>
    /// Provides the MaximumSummaryLength contract constant.
    /// </summary>
    public const int MaximumSummaryLength = 8_192;
    /// <summary>
    /// Provides the MaximumPropertyValueLength contract constant.
    /// </summary>
    public const int MaximumPropertyValueLength = 16_384;

    public static readonly IEqualityComparer<WorkerInvocationEvidence> InvocationComparer =
        new DelegateComparer<WorkerInvocationEvidence>(InvocationEqual);
    public static readonly IEqualityComparer<ValidationEvidence> ValidationComparer =
        new DelegateComparer<ValidationEvidence>(ValidationEqual);
    public static readonly IEqualityComparer<ReviewEvidence> ReviewComparer =
        new DelegateComparer<ReviewEvidence>(ReviewEqual);

    /// <summary>
    /// Performs the Name contract operation.
    /// </summary>
    public static string Name(string? value, string parameterName) =>
        ArtifactContracts.Version(value, parameterName);

    /// <summary>
    /// Performs the OptionalName contract operation.
    /// </summary>
    public static string? OptionalName(string? value, string parameterName) =>
        value is null ? null : Name(value, parameterName);

    /// <summary>
    /// Performs the Identifier contract operation.
    /// </summary>
    public static string Identifier(string? value, string parameterName) =>
        IdentityText.Require(value, parameterName, 512);

    /// <summary>
    /// Performs the OptionalIdentifier contract operation.
    /// </summary>
    public static string? OptionalIdentifier(string? value, string parameterName) =>
        value is null ? null : Identifier(value, parameterName);

    /// <summary>
    /// Performs the Names contract operation.
    /// </summary>
    public static IReadOnlyList<string> Names(IReadOnlyList<string> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (values.Count > MaximumProperties)
        {
            throw new ArgumentException($"A name collection cannot contain more than {MaximumProperties} values.", parameterName);
        }

        var result = values.Select(value => Identifier(value, parameterName)).ToArray();
        if (result.Distinct(StringComparer.Ordinal).Count() != result.Length)
        {
            throw new ArgumentException("A name collection cannot contain duplicate values.", parameterName);
        }

        return Array.AsReadOnly(result);
    }

    /// <summary>
    /// Performs the Artifacts contract operation.
    /// </summary>
    public static IReadOnlyList<DelegationArtifactReference> Artifacts(
        IReadOnlyList<DelegationArtifactReference> values,
        DelegationId delegationId,
        string parameterName,
        StructuralNodeReference? expectedNode = null,
        NodeGenerationId? expectedGeneration = null)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (values.Count > MaximumArtifacts)
        {
            throw new ArgumentException($"An evidence artifact collection cannot contain more than {MaximumArtifacts} values.", parameterName);
        }

        var result = values.ToArray();
        var identities = new HashSet<(string Provider, string Repository, string ArtifactId)>();
        foreach (var artifact in result)
        {
            ArtifactContracts.ValidateArtifact(artifact, delegationId, expectedNode, expectedGeneration);
            if (!identities.Add((artifact.Provider, artifact.Repository, artifact.ArtifactId)))
            {
                throw new ArgumentException("Evidence cannot contain duplicate artifact identities.", parameterName);
            }
        }

        return Array.AsReadOnly(result);
    }

    /// <summary>
    /// Performs the Findings contract operation.
    /// </summary>
    public static IReadOnlyList<EvidenceFinding> Findings(
        IReadOnlyList<EvidenceFinding> values,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (values.Count > MaximumFindings)
        {
            throw new ArgumentException($"An evidence finding collection cannot contain more than {MaximumFindings} values.", parameterName);
        }

        var result = values.ToArray();
        foreach (var finding in result)
        {
            ArgumentNullException.ThrowIfNull(finding, parameterName);
        }

        return Array.AsReadOnly(result);
    }

    public static IReadOnlyList<T> Evidence<T>(IReadOnlyList<T>? values, string parameterName)
        where T : class
    {
        if (values is null || values.Count == 0)
        {
            return Array.Empty<T>();
        }

        if (values.Count > MaximumFindings)
        {
            throw new ArgumentException(
                $"An evidence bundle cannot contain more than {MaximumFindings} records of one kind.",
                parameterName);
        }

        var result = values.ToArray();
        foreach (var value in result)
        {
            ArgumentNullException.ThrowIfNull(value, parameterName);
        }

        return Array.AsReadOnly(result);
    }

    /// <summary>
    /// Performs the RequireUniqueInvocations contract operation.
    /// </summary>
    public static void RequireUniqueInvocations(
        IReadOnlyList<WorkerInvocationEvidence> values,
        string parameterName)
    {
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!identities.Add(value.Attempt.AttemptId))
            {
                throw new ArgumentException(
                    "An evidence bundle cannot contain duplicate invocation identities.",
                    parameterName);
            }
        }
    }

    /// <summary>
    /// Performs the RequireUniqueValidations contract operation.
    /// </summary>
    public static void RequireUniqueValidations(
        IReadOnlyList<ValidationEvidence> values,
        string parameterName)
    {
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!identities.Add(value.Invocation.Attempt.AttemptId))
            {
                throw new ArgumentException(
                    "An evidence bundle cannot contain duplicate validation invocation identities.",
                    parameterName);
            }
        }
    }

    /// <summary>
    /// Performs the RequireUniqueReviews contract operation.
    /// </summary>
    public static void RequireUniqueReviews(
        IReadOnlyList<ReviewEvidence> values,
        string parameterName)
    {
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!identities.Add(value.Invocation.Attempt.AttemptId))
            {
                throw new ArgumentException(
                    "An evidence bundle cannot contain duplicate review invocation identities.",
                    parameterName);
            }
        }
    }

    /// <summary>
    /// Performs the Properties contract operation.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Properties(
        IReadOnlyDictionary<string, string>? values,
        string parameterName)
    {
        if (values is null || values.Count == 0)
        {
            return ReadOnlyProperties(new Dictionary<string, string>(StringComparer.Ordinal));
        }

        if (values.Count > MaximumProperties)
        {
            throw new ArgumentException($"An evidence property collection cannot contain more than {MaximumProperties} values.", parameterName);
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in values)
        {
            var key = Name(pair.Key, parameterName);
            var value = IdentityText.RequireProse(pair.Value, parameterName, MaximumPropertyValueLength);
            if (!result.TryAdd(key, value))
            {
                throw new ArgumentException("Evidence properties cannot contain duplicate keys.", parameterName);
            }
        }

        return ReadOnlyProperties(result);
    }

    /// <summary>
    /// Performs the ValidateCandidate contract operation.
    /// </summary>
    public static void ValidateCandidate(
        CandidateRevisionReference? candidate,
        WorkerInvocationEvidence invocation,
        string parameterName)
    {
        if (candidate is null)
        {
            return;
        }

        if (candidate.DelegationId != invocation.DelegationId
            || candidate.StructuralNode != invocation.StructuralNode
            || candidate.NodeGeneration != invocation.NodeGeneration)
        {
            throw new ArgumentException(
                "A candidate reference must belong to the invocation delegation, node, and generation.",
                parameterName);
        }

        if (invocation.Candidate is not null
            && !CandidateRevisionIdentity.SubjectEqual(candidate, invocation.Candidate))
        {
            throw new ArgumentException(
                "An explicit candidate must exactly match the invocation candidate reference.",
                parameterName);
        }
    }

    /// <summary>
    /// Performs the ValidateBundle contract operation.
    /// </summary>
    public static void ValidateBundle(
        EvidenceBundle? bundle,
        CandidateRevisionReference candidate,
        string parameterName)
    {
        if (bundle is null)
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(candidate);
        foreach (var invocation in bundle.Invocations)
        {
            ValidateInvocationOwner(invocation, candidate.DelegationId, candidate.StructuralNode, candidate.NodeGeneration, parameterName);
            if (invocation.Candidate is not null
                && !CandidateRevisionIdentity.SubjectEqual(candidate, invocation.Candidate))
            {
                throw new ArgumentException(
                    "Evidence invocation candidate must match the published candidate subject.",
                    parameterName);
            }
        }

        foreach (var validation in bundle.Validations)
        {
            ValidateInvocationOwner(validation.Invocation, candidate.DelegationId, candidate.StructuralNode, candidate.NodeGeneration, parameterName);
            if (validation.Candidate is not null
                && !CandidateRevisionIdentity.SubjectEqual(candidate, validation.Candidate))
            {
                throw new ArgumentException(
                    "Validation evidence candidate must match the published candidate subject.",
                    parameterName);
            }
        }

        foreach (var review in bundle.Reviews)
        {
            ValidateInvocationOwner(review.Invocation, candidate.DelegationId, candidate.StructuralNode, candidate.NodeGeneration, parameterName);
            if (!CandidateRevisionIdentity.SubjectEqual(candidate, review.Candidate))
            {
                throw new ArgumentException(
                    "Review evidence candidate must match the published candidate subject.",
                    parameterName);
            }
        }
    }

    /// <summary>
    /// Performs the ValidateBundleForDelegation contract operation.
    /// </summary>
    public static void ValidateBundleForDelegation(
        EvidenceBundle? bundle,
        DelegationId delegationId,
        string parameterName)
    {
        if (bundle is null)
        {
            return;
        }

        foreach (var invocation in bundle.Invocations)
        {
            if (invocation.DelegationId != delegationId)
            {
                throw new ArgumentException("Evidence invocation ownership does not match the result delegation.", parameterName);
            }
        }

        foreach (var validation in bundle.Validations)
        {
            if (validation.Invocation.DelegationId != delegationId
                || validation.Candidate?.DelegationId is { } candidateDelegation && candidateDelegation != delegationId)
            {
                throw new ArgumentException("Validation evidence ownership does not match the result delegation.", parameterName);
            }
        }

        foreach (var review in bundle.Reviews)
        {
            if (review.Invocation.DelegationId != delegationId
                || review.Candidate.DelegationId != delegationId)
            {
                throw new ArgumentException("Review evidence ownership does not match the result delegation.", parameterName);
            }
        }
    }

    private static void ValidateInvocationOwner(
        WorkerInvocationEvidence invocation,
        DelegationId delegationId,
        StructuralNodeReference node,
        NodeGenerationId generation,
        string parameterName)
    {
        if (invocation.DelegationId != delegationId
            || invocation.StructuralNode != node
            || invocation.NodeGeneration != generation)
        {
            throw new ArgumentException(
                "Evidence invocation ownership does not match the published candidate.",
                parameterName);
        }
    }

    private static bool InvocationEqual(WorkerInvocationEvidence left, WorkerInvocationEvidence right) =>
        left.DelegationId == right.DelegationId
        && left.StructuralNode == right.StructuralNode
        && left.NodeGeneration == right.NodeGeneration
        && string.Equals(left.ExecutionCategory, right.ExecutionCategory, StringComparison.Ordinal)
        && left.Attempt == right.Attempt
        && string.Equals(left.Disposition, right.Disposition, StringComparison.Ordinal)
        && left.StartedAt == right.StartedAt
        && left.CompletedAt == right.CompletedAt
        && string.Equals(left.Capability, right.Capability, StringComparison.Ordinal)
        && string.Equals(left.Profile, right.Profile, StringComparison.Ordinal)
        && string.Equals(left.RequestedProvider, right.RequestedProvider, StringComparison.Ordinal)
        && string.Equals(left.RequestedModel, right.RequestedModel, StringComparison.Ordinal)
        && string.Equals(left.ResolvedModel, right.ResolvedModel, StringComparison.Ordinal)
        && left.ToolCapabilities.SequenceEqual(right.ToolCapabilities, StringComparer.Ordinal)
        && left.InputArtifacts.SequenceEqual(right.InputArtifacts)
        && left.OutputArtifacts.SequenceEqual(right.OutputArtifacts)
        && CorrelationEqual(left.ExecutionCorrelation, right.ExecutionCorrelation)
        && CandidateRevisionIdentity.SubjectEqual(left.Candidate, right.Candidate)
        && PropertiesEqual(left.Usage, right.Usage)
        && PropertiesEqual(left.ProviderData, right.ProviderData);

    private static bool ValidationEqual(ValidationEvidence left, ValidationEvidence right) =>
        InvocationEqual(left.Invocation, right.Invocation)
        && string.Equals(left.Outcome, right.Outcome, StringComparison.Ordinal)
        && string.Equals(left.Validator, right.Validator, StringComparison.Ordinal)
        && left.Findings.SequenceEqual(right.Findings, FindingComparer)
        && CandidateRevisionIdentity.SubjectEqual(left.Candidate, right.Candidate);

    private static bool ReviewEqual(ReviewEvidence left, ReviewEvidence right) =>
        InvocationEqual(left.Invocation, right.Invocation)
        && string.Equals(left.Outcome, right.Outcome, StringComparison.Ordinal)
        && left.Findings.SequenceEqual(right.Findings, FindingComparer)
        && IndependenceEqual(left.Independence, right.Independence)
        && CandidateRevisionIdentity.SubjectEqual(left.Candidate, right.Candidate)
        && string.Equals(left.Reviewer, right.Reviewer, StringComparison.Ordinal);

    private static readonly IEqualityComparer<EvidenceFinding> FindingComparer =
        new DelegateComparer<EvidenceFinding>((left, right) =>
            string.Equals(left.Code, right.Code, StringComparison.Ordinal)
            && string.Equals(left.Severity, right.Severity, StringComparison.Ordinal)
            && string.Equals(left.Summary, right.Summary, StringComparison.Ordinal)
            && left.Resolved == right.Resolved
            && PropertiesEqual(left.Details, right.Details));

    private static bool IndependenceEqual(ReviewIndependenceEvidence left, ReviewIndependenceEvidence right) =>
        string.Equals(left.ImplementationInvocationId, right.ImplementationInvocationId, StringComparison.Ordinal)
        && string.Equals(left.ReviewInvocationId, right.ReviewInvocationId, StringComparison.Ordinal)
        && left.Invocation == right.Invocation
        && left.Context == right.Context
        && left.Profile == right.Profile
        && left.Model == right.Model
        && left.Provider == right.Provider
        && PropertiesEqual(left.Details, right.Details);

    private static bool PropertiesEqual(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count
        && left.All(pair => right.TryGetValue(pair.Key, out var value)
            && string.Equals(pair.Value, value, StringComparison.Ordinal));

    private static bool CorrelationEqual(
        ExternalOperationCorrelation? left,
        ExternalOperationCorrelation? right) =>
        ReferenceEquals(left, right)
        || (left is not null
            && right is not null
            && left.DelegationId == right.DelegationId
            && left.WorkflowRun == right.WorkflowRun
            && left.StructuralNode == right.StructuralNode
            && left.NodeGeneration == right.NodeGeneration
            && string.Equals(left.ExecutionAttemptId, right.ExecutionAttemptId, StringComparison.Ordinal)
            && left.Agent == right.Agent
            && left.Task == right.Task);

    private sealed class DelegateComparer<T>(Func<T, T, bool> equals) : IEqualityComparer<T>
    {
        /// <summary>
        /// Performs the Equals contract operation.
        /// </summary>
        public bool Equals(T? x, T? y) => x is not null && y is not null && equals(x, y);
        /// <summary>
        /// Performs the GetHashCode contract operation.
        /// </summary>
        public int GetHashCode(T obj) => throw new NotSupportedException("Evidence identity does not use hash-based comparisons.");
    }

    private static IReadOnlyDictionary<string, string> ReadOnlyProperties(Dictionary<string, string> values) =>
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(values);
}
