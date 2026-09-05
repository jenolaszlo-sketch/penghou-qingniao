using FluentAssertions;

namespace Penghou.Qingniao.Tests;

public sealed class EvidenceContractsTests
{
    [Fact]
    public void Worker_invocation_snapshots_bounded_extensible_data_and_preserves_requested_and_resolved_identity()
    {
        var invocation = CreateInvocation(
            requestedProvider: "host-profile",
            requestedModel: "review-model",
            resolvedModel: "provider/model-v3",
            tools: ["read-files", "run-tests"],
            usage: new Dictionary<string, string> { ["input_tokens"] = "12" },
            providerData: new Dictionary<string, string> { ["provider.request_id"] = "req-1" });

        invocation.RequestedProvider.Should().Be("host-profile");
        invocation.RequestedModel.Should().Be("review-model");
        invocation.ResolvedModel.Should().Be("provider/model-v3");
        invocation.ToolCapabilities.Should().Equal("read-files", "run-tests");
        invocation.Usage["input_tokens"].Should().Be("12");

        invocation.ToolCapabilities.Should().NotBeSameAs(new[] { "read-files", "run-tests" });
        var usage = new Dictionary<string, string> { ["input_tokens"] = "12" };
        var copied = CreateInvocation(usage: usage);
        usage["input_tokens"] = "99";
        copied.Usage["input_tokens"].Should().Be("12");

        var empty = (System.Collections.IDictionary)CreateInvocation().ProviderData;
        var mutateEmpty = () => empty.Add("provider.secret", "must-fail");
        mutateEmpty.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Worker_invocation_requires_owned_unique_artifacts_and_candidate()
    {
        var artifact = CreateArtifact();
        var duplicate = () => CreateInvocation(inputArtifacts: [artifact, artifact]);
        var wrongCandidate = () => CreateInvocation(candidate: CreateCandidate(delegationId: OtherDelegationId));

        duplicate.Should().Throw<ArgumentException>();
        wrongCandidate.Should().Throw<ArgumentException>();

        var wrongNodeArtifact = new DelegationArtifactReference(
            DelegationIdValue,
            new StructuralNodeReference("other-node"),
            Generation,
            "provider",
            "repository",
            "other-artifact",
            "evidence",
            1,
            "artifact-location/other-artifact",
            ArtifactContentIdentity.Sha256Bytes(Hash));
        var wrongOutput = () => CreateInvocation(outputArtifacts: [wrongNodeArtifact]);
        wrongOutput.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void External_execution_correlation_must_match_invocation_owner_and_attempt()
    {
        var correlation = new ExternalOperationCorrelation(
            DelegationIdValue,
            new WorkflowRunExecutionReference("zhinu", "run-1", "epoch-1"),
            Node,
            Generation,
            "review-attempt",
            new ExternalAgentReference("provider", "agent-1", "protocol-v1"),
            new ExternalTaskReference("provider", "task-1"));
        var invocation = CreateInvocation(executionCorrelation: correlation);
        invocation.ExecutionCorrelation.Should().Be(correlation);

        var wrongOwner = new ExternalOperationCorrelation(
            OtherDelegationId,
            correlation.WorkflowRun,
            Node,
            Generation,
            "review-attempt",
            correlation.Agent,
            correlation.Task);
        var mismatch = () => CreateInvocation(executionCorrelation: wrongOwner);
        mismatch.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Worker_invocation_rejects_invalid_time_category_and_bounds()
    {
        var badTime = () => CreateInvocation(
            startedAt: DateTimeOffset.UtcNow,
            completedAt: DateTimeOffset.UtcNow.AddSeconds(-1));
        var badCategory = () => CreateInvocation(executionCategory: "Agent.Execution");
        var tooManyTools = () => CreateInvocation(tools: Enumerable.Range(0, 65).Select(i => $"tool_{i}").ToArray());
        var duplicateTools = () => CreateInvocation(tools: ["read-files", "read-files"]);

        badTime.Should().Throw<ArgumentException>();
        badCategory.Should().Throw<ArgumentException>();
        tooManyTools.Should().Throw<ArgumentException>();
        duplicateTools.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Validation_evidence_accepts_only_deterministic_invocations_and_snapshots_findings()
    {
        var findings = new List<EvidenceFinding> { new("compile.error", "error", "failed", false) };
        var validation = new ValidationEvidence(CreateInvocation(executionCategory: EvidenceKinds.DeterministicExecution), "failed", findings);
        findings.Clear();

        validation.Findings.Should().ContainSingle();
        var wrong = () => new ValidationEvidence(CreateInvocation(executionCategory: EvidenceKinds.ModelExecution), "passed", []);
        wrong.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Review_evidence_requires_subject_candidate_reviewer_and_matching_invocation()
    {
        var candidate = CreateCandidate();
        var invocation = CreateInvocation(executionCategory: EvidenceKinds.ModelExecution, candidate: candidate);
        var independence = new ReviewIndependenceEvidence(
            "implementation-attempt",
            "review-attempt",
            IndependenceAssessment.Different,
            IndependenceAssessment.Different,
            IndependenceAssessment.Different,
            IndependenceAssessment.Different,
            IndependenceAssessment.Different);
        var review = new ReviewEvidence(invocation, "approved", [], independence, candidate, "reviewer-1");

        review.Candidate.Should().Be(candidate);
        review.Reviewer.Should().Be("reviewer-1");

        var wrongInvocation = new ReviewIndependenceEvidence(
            "implementation-attempt",
            "other-attempt",
            IndependenceAssessment.Different,
            IndependenceAssessment.Different,
            IndependenceAssessment.Different,
            IndependenceAssessment.Different,
            IndependenceAssessment.Different);
        var mismatch = () => new ReviewEvidence(invocation, "approved", [], wrongInvocation, candidate, "reviewer-1");
        mismatch.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Explicit_candidates_must_equal_the_invocation_candidate_not_only_share_ownership()
    {
        var invocationCandidate = CreateCandidate("invocation-candidate");
        var conflictingCandidate = CreateCandidate("conflicting-candidate");
        var validationInvocation = CreateInvocation(
            executionCategory: EvidenceKinds.DeterministicExecution,
            candidate: invocationCandidate);
        var reviewInvocation = CreateInvocation(
            executionCategory: EvidenceKinds.ModelExecution,
            candidate: invocationCandidate);
        var independence = new ReviewIndependenceEvidence(
            "implementation-attempt",
            "review-attempt",
            IndependenceAssessment.Different,
            IndependenceAssessment.Different,
            IndependenceAssessment.Different,
            IndependenceAssessment.Different,
            IndependenceAssessment.Different);

        var validation = () => new ValidationEvidence(
            validationInvocation,
            "passed",
            [],
            candidate: conflictingCandidate);
        var review = () => new ReviewEvidence(
            reviewInvocation,
            "approved",
            [],
            independence,
            conflictingCandidate,
            "reviewer-1");

        validation.Should().Throw<ArgumentException>();
        review.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Independence_evidence_rejects_claims_that_contradict_invocation_identities()
    {
        var sameClaim = () => new ReviewIndependenceEvidence(
            "same",
            "same",
            IndependenceAssessment.Different,
            IndependenceAssessment.Unknown,
            IndependenceAssessment.Unknown,
            IndependenceAssessment.Unknown,
            IndependenceAssessment.Unknown);
        var differentClaim = () => new ReviewIndependenceEvidence(
            "one",
            "two",
            IndependenceAssessment.Same,
            IndependenceAssessment.Unknown,
            IndependenceAssessment.Unknown,
            IndependenceAssessment.Unknown,
            IndependenceAssessment.Unknown);

        sameClaim.Should().Throw<ArgumentException>();
        differentClaim.Should().Throw<ArgumentException>();
    }

    private static WorkerInvocationEvidence CreateInvocation(
        string executionCategory = EvidenceKinds.AgentExecution,
        string? requestedProvider = null,
        string? requestedModel = null,
        string? resolvedModel = null,
        IReadOnlyList<string>? tools = null,
        IReadOnlyList<DelegationArtifactReference>? inputArtifacts = null,
        IReadOnlyList<DelegationArtifactReference>? outputArtifacts = null,
        CandidateRevisionReference? candidate = null,
        IReadOnlyDictionary<string, string>? usage = null,
        IReadOnlyDictionary<string, string>? providerData = null,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? completedAt = null,
        ExternalOperationCorrelation? executionCorrelation = null) => new(
        DelegationIdValue,
        Node,
        Generation,
        executionCategory,
        new ProviderExecutionAttemptReference("provider", "review-attempt", "opaque-handle"),
        "completed",
        startedAt ?? DateTimeOffset.UtcNow,
        completedAt ?? DateTimeOffset.UtcNow.AddSeconds(1),
        "review-code",
        "review",
        requestedProvider,
        requestedModel,
        resolvedModel,
        tools ?? ["read-files"],
        inputArtifacts ?? [CreateArtifact()],
        outputArtifacts ?? [],
        candidate,
        usage,
         providerData,
         executionCorrelation);

    private static DelegationArtifactReference CreateArtifact(
        string artifactId = "artifact-1",
        DelegationId? delegationId = null) => new(
        delegationId ?? DelegationIdValue,
        Node,
        Generation,
        "provider",
        "repository",
        artifactId,
        "evidence",
        1,
        $"artifact-location/{artifactId}",
        ArtifactContentIdentity.Sha256Bytes(Hash));

    private static CandidateRevisionReference CreateCandidate(
        string artifactId = "artifact-1",
        DelegationId? delegationId = null) => new(
        delegationId ?? DelegationIdValue,
        Node,
        Generation,
        new CandidateId(CandidateIdValue),
        1,
        ArtifactContentIdentity.Sha256Bytes(Hash),
        [CreateArtifact(artifactId, delegationId)]);

    private static readonly DelegationId DelegationIdValue = new(Guid.Parse("00000000-0000-0000-0000-000000000001"));
    private static readonly DelegationId OtherDelegationId = new(Guid.Parse("00000000-0000-0000-0000-000000000002"));
    private static readonly StructuralNodeReference Node = new("implement");
    private static readonly NodeGenerationId Generation = new(Guid.Parse("00000000-0000-0000-0000-000000000011"));
    private static readonly Guid CandidateIdValue = Guid.Parse("00000000-0000-0000-0000-000000000021");
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
}
