using FluentAssertions;

namespace Penghou.Qingniao.Tests;

public sealed class EvidencePublicationTests
{
    [Fact]
    public async Task Candidate_publication_binds_the_evidence_bundle_and_rejects_changed_evidence()
    {
        var candidate = CreateCandidate();
        var bundle = CreateBundle(candidate, providerData: "request-1");
        var published = CreateCandidate(evidence: bundle);
        var replay = CreateCandidate(evidence: CreateBundle(candidate, providerData: "request-1"));
        var changed = CreateCandidate(evidence: CreateBundle(candidate, providerData: "request-2"));
        var registry = new InMemoryCandidateRevisionPublicationRegistry();

        var first = await registry.PublishAsync(published, TestContext.Current.CancellationToken);
        var exactReplay = await registry.PublishAsync(replay, TestContext.Current.CancellationToken);

        first.IsNew.Should().BeTrue();
        exactReplay.IsNew.Should().BeFalse();
        exactReplay.Candidate.Evidence.Should().NotBeNull();
        var result = new DelegationResultReference(
            DelegationIdValue,
            new DelegationResultId(ResultIdValue),
            published,
            [],
            bundle);
        result.Evidence.Should().NotBeNull();

        Func<Task<CandidateRevisionPublication>> conflictingPublication = () =>
            registry.PublishAsync(changed, TestContext.Current.CancellationToken).AsTask();
        await conflictingPublication.Should().ThrowAsync<CandidateRevisionConflictException>();
    }

    [Fact]
    public void Candidate_publication_is_node_scoped_but_result_publication_is_delegation_scoped()
    {
        var candidate = CreateCandidate();
        var wrongInvocation = CreateInvocation(
            null,
            node: new StructuralNodeReference("review"));
        var wrongBundle = new EvidenceBundle([wrongInvocation]);

        var candidatePublication = () => CreateCandidate(evidence: wrongBundle);
        candidatePublication.Should().Throw<ArgumentException>();

        var resultPublication = () => new DelegationResultReference(
            DelegationIdValue,
            new DelegationResultId(ResultIdValue),
            candidate,
            [],
            wrongBundle);
        resultPublication.Should().NotThrow();

        var wrongDelegationBundle = new EvidenceBundle(
            [CreateInvocation(null, node: new StructuralNodeReference("review"), delegationId: OtherDelegationId)]);
        var wrongDelegationPublication = () => new DelegationResultReference(
            DelegationIdValue,
            new DelegationResultId(ResultIdValue),
            candidate,
            [],
            wrongDelegationBundle);
        wrongDelegationPublication.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Terminal_result_equality_includes_normalized_evidence_without_reopening_terminal_state()
    {
        var candidate = CreateCandidate();
        var evidence = CreateBundle(candidate, providerData: "request-1");
        var completedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var existing = CreateResult(evidence, completedAt);
        var replay = CreateResult(CreateBundle(candidate, providerData: "request-1"), completedAt);
        var changed = CreateResult(CreateBundle(candidate, providerData: "request-2"), completedAt);

        DelegationLifecycle.ValidateResultPublication(existing, replay);
        var replacement = () => DelegationLifecycle.ValidateResultPublication(existing, changed);
        replacement.Should().Throw<DelegationLifecycleViolationException>();
    }

    [Fact]
    public void Evidence_bundle_snapshots_lists_and_rejects_duplicate_invocation_identity()
    {
        var candidate = CreateCandidate();
        var invocation = CreateInvocation(candidate);
        var source = new List<WorkerInvocationEvidence> { invocation };
        var bundle = new EvidenceBundle(source);
        source.Clear();

        bundle.Invocations.Should().ContainSingle();
        var duplicate = () => new EvidenceBundle([invocation, invocation]);
        duplicate.Should().Throw<ArgumentException>();
    }

    private static EvidenceBundle CreateBundle(
        CandidateRevisionReference candidate,
        string providerData,
        string implementationAttempt = "implementation-attempt")
    {
        var implementation = CreateInvocation(candidate, attemptId: implementationAttempt, providerData: providerData);
        var validation = new ValidationEvidence(
            CreateInvocation(candidate, EvidenceKinds.DeterministicExecution, "test-attempt"),
            "passed",
            []);
        var reviewInvocation = CreateInvocation(candidate, EvidenceKinds.ModelExecution, "review-attempt");
        var independence = new ReviewIndependenceEvidence(
            implementation.Attempt.AttemptId,
            reviewInvocation.Attempt.AttemptId,
            IndependenceAssessment.Different,
            IndependenceAssessment.Different,
            IndependenceAssessment.Different,
            IndependenceAssessment.Different,
            IndependenceAssessment.Different);
        var review = new ReviewEvidence(
            reviewInvocation,
            "approved",
            [],
            independence,
            candidate,
            "reviewer-1");

        return new EvidenceBundle([implementation], [validation], [review]);
    }

    private static WorkerInvocationEvidence CreateInvocation(
        CandidateRevisionReference? candidate,
        string executionCategory = EvidenceKinds.AgentExecution,
        string attemptId = "implementation-attempt",
        StructuralNodeReference? node = null,
        string? providerData = null,
        DelegationId? delegationId = null) => new(
        delegationId ?? DelegationIdValue,
        node ?? Node,
        Generation,
        executionCategory,
        new ProviderExecutionAttemptReference("provider", attemptId, $"handle-{attemptId}"),
        "completed",
        DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
        DateTimeOffset.Parse("2026-01-01T00:00:01Z"),
        "capability",
        "profile",
        "requested-provider",
        "requested-model",
        "resolved-model",
        ["read-files"],
        [CreateArtifact(delegationId: delegationId)],
        [],
        candidate,
        null,
        providerData is null ? null : new Dictionary<string, string> { ["provider.request_id"] = providerData });

    private static DelegationResult CreateResult(EvidenceBundle evidence, DateTimeOffset completedAt) => new(
        DelegationIdValue,
        DelegationState.Completed,
        "summary",
        new DelegationEvidence([], [], 1, 0, true, 0),
        [],
        [],
        completedAt,
        evidence);

    private static CandidateRevisionReference CreateCandidate(EvidenceBundle? evidence = null)
    {
        return new CandidateRevisionReference(
            DelegationIdValue,
            Node,
            Generation,
            new CandidateId(CandidateIdValue),
            1,
            ArtifactContentIdentity.Sha256Bytes(Hash),
            [CreateArtifact()],
            evidence);
    }

    private static DelegationArtifactReference CreateArtifact(DelegationId? delegationId = null) => new(
        delegationId ?? DelegationIdValue,
        Node,
        Generation,
        "provider",
        "repository",
        "artifact-1",
        "application/json",
        1,
        "artifact-location",
        ArtifactContentIdentity.Sha256Bytes(Hash));

    private static readonly DelegationId DelegationIdValue = new(
        Guid.Parse("00000000-0000-0000-0000-000000000001"));
    private static readonly DelegationId OtherDelegationId = new(
        Guid.Parse("00000000-0000-0000-0000-000000000002"));
    private static readonly StructuralNodeReference Node = new("implement");
    private static readonly NodeGenerationId Generation = new(
        Guid.Parse("00000000-0000-0000-0000-000000000011"));
    private static readonly Guid CandidateIdValue =
        Guid.Parse("00000000-0000-0000-0000-000000000021");
    private static readonly Guid ResultIdValue =
        Guid.Parse("00000000-0000-0000-0000-000000000020");
    private const string Hash =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
}
