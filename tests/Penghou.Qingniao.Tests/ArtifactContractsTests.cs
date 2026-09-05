using FluentAssertions;

namespace Penghou.Qingniao.Tests;

public sealed class ArtifactContractsTests
{
    [Fact]
    public void Artifact_identity_requires_owner_kind_schema_location_and_hash_contract()
    {
        var artifact = CreateArtifact();

        artifact.DelegationId.Should().Be(DelegationIdValue);
        artifact.ContentIdentity.Should().Be(ArtifactContentIdentity.Sha256Bytes(Hash));

        var badHash = () => ArtifactContentIdentity.Sha256Bytes("ABC");
        var badSchema = () => CreateArtifact(schemaVersion: 0);
        var badLocation = () => CreateArtifact(location: "location\nnext");

        badHash.Should().Throw<ArgumentException>();
        badSchema.Should().Throw<ArgumentOutOfRangeException>();
        badLocation.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Artifact_identity_rejects_control_characters_without_delimiter_collisions()
    {
        var badProvider = () => CreateArtifact(provider: "provider\u001fother");

        badProvider.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Artifact_and_candidate_ownership_must_match()
    {
        var wrongOwner = CreateArtifact(delegationId: OtherDelegationId);
        var wrongNode = CreateArtifact(node: new StructuralNodeReference("review"));
        var wrongGeneration = CreateArtifact(generation: OtherGeneration);

        var wrongDelegation = () => CreateCandidate([wrongOwner]);
        var wrongStructuralNode = () => CreateCandidate([wrongNode]);
        var wrongNodeGeneration = () => CreateCandidate([wrongGeneration]);

        wrongDelegation.Should().Throw<ArgumentException>();
        wrongStructuralNode.Should().Throw<ArgumentException>();
        wrongNodeGeneration.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Candidate_requires_artifacts_and_rejects_duplicate_artifact_identity()
    {
        var noArtifacts = () => CreateCandidate([]);
        var duplicates = () => CreateCandidate(
        [
            CreateArtifact(),
            CreateArtifact(),
        ]);

        noArtifacts.Should().Throw<ArgumentException>();
        duplicates.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Candidate_and_result_snapshot_references_and_allow_external_same_delegation_evidence()
    {
        var source = new List<DelegationArtifactReference> { CreateArtifact() };
        var candidate = CreateCandidate(source);
        source.Clear();

        candidate.Artifacts.Should().ContainSingle();

        var reviewEvidence = CreateArtifact(
            artifactId: "review-artifact",
            node: new StructuralNodeReference("review"),
            generation: OtherGeneration);
        var resultArtifacts = new List<DelegationArtifactReference> { reviewEvidence };
        var result = new DelegationResultReference(
            DelegationIdValue,
            new DelegationResultId(ResultIdValue),
            candidate,
            resultArtifacts);
        resultArtifacts.Clear();

        result.Artifacts.Should().ContainSingle().Which.Should().Be(reviewEvidence);

        var wrongDelegation = () => new DelegationResultReference(
            DelegationIdValue,
            new DelegationResultId(ResultIdValue),
            candidate,
            [CreateArtifact(delegationId: OtherDelegationId)]);
        var duplicateArtifacts = () => new DelegationResultReference(
            DelegationIdValue,
            new DelegationResultId(ResultIdValue),
            candidate,
            [CreateArtifact(), CreateArtifact()]);

        wrongDelegation.Should().Throw<ArgumentException>();
        duplicateArtifacts.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Hash_contract_versions_are_preserved_without_reinterpretation()
    {
        var legacy = new ArtifactContentIdentity("legacy-external-v1", "provider-specific-value");

        legacy.ContractVersion.Should().Be("legacy-external-v1");
        legacy.Hash.Should().Be("provider-specific-value");

        var invalidVersion = () => new ArtifactContentIdentity("V1", Hash);
        var invalidJsonContract = () => new ArtifactContentIdentity("sha256-json-v2", "provider-specific-value");

        invalidVersion.Should().Throw<ArgumentException>();
        invalidJsonContract.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Candidate_publication_is_idempotent_for_equal_content_and_rejects_conflicts()
    {
        var registry = new InMemoryCandidateRevisionPublicationRegistry();
        var cancellationToken = TestContext.Current.CancellationToken;
        var first = await registry.PublishAsync(CreateCandidate([CreateArtifact()]), cancellationToken);
        var duplicate = await registry.PublishAsync(CreateCandidate([CreateArtifact()]), cancellationToken);

        first.IsNew.Should().BeTrue();
        duplicate.IsNew.Should().BeFalse();
        CandidateRevisionIdentity.SemanticallyEqual(duplicate.Candidate, first.Candidate).Should().BeTrue();

        Func<Task<CandidateRevisionPublication>> conflict = () => registry.PublishAsync(
            CreateCandidate([CreateArtifact("different-artifact")]), cancellationToken).AsTask();

        await conflict.Should().ThrowAsync<CandidateRevisionConflictException>();
    }

    [Fact]
    public async Task Candidate_publication_allows_delegation_scoped_candidates_and_revisions()
    {
        var registry = new InMemoryCandidateRevisionPublicationRegistry();
        var cancellationToken = TestContext.Current.CancellationToken;

        var revisionOne = await registry.PublishAsync(
            CreateCandidate(), cancellationToken);
        var sameIdOtherDelegation = await registry.PublishAsync(
            CreateCandidate(delegationId: OtherDelegationId), cancellationToken);
        var revisionTwo = await registry.PublishAsync(
            CreateCandidate(revision: 2, artifactId: "revision-two"), cancellationToken);

        revisionOne.IsNew.Should().BeTrue();
        sameIdOtherDelegation.IsNew.Should().BeTrue();
        revisionTwo.IsNew.Should().BeTrue();
    }

    [Fact]
    public async Task Candidate_publication_honors_cancellation_before_mutation()
    {
        var registry = new InMemoryCandidateRevisionPublicationRegistry();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Func<Task<CandidateRevisionPublication>> publish = () => registry.PublishAsync(
            CreateCandidate(), cancellation.Token).AsTask();

        await publish.Should().ThrowAsync<OperationCanceledException>();

        var accepted = await registry.PublishAsync(
            CreateCandidate(), TestContext.Current.CancellationToken);
        accepted.IsNew.Should().BeTrue();
    }

    [Fact]
    public async Task Candidate_publication_is_atomic_for_competing_submissions()
    {
        var registry = new InMemoryCandidateRevisionPublicationRegistry();
        var cancellationToken = TestContext.Current.CancellationToken;
        var candidates = Enumerable.Range(0, 32)
            .Select(_ => CreateCandidate([CreateArtifact()]))
            .ToArray();

        var results = await Task.WhenAll(candidates.Select(candidate =>
            registry.PublishAsync(candidate, cancellationToken).AsTask()));

        results.Count(result => result.IsNew).Should().Be(1);
        results.Select(result => result.Candidate.CandidateId).Distinct().Should().ContainSingle();
    }

    private static readonly DelegationId DelegationIdValue = new(
        Guid.Parse("00000000-0000-0000-0000-000000000001"));
    private static readonly DelegationId OtherDelegationId = new(
        Guid.Parse("00000000-0000-0000-0000-000000000002"));
    private static readonly StructuralNodeReference Node = new("implement");
    private static readonly NodeGenerationId Generation = new(
        Guid.Parse("00000000-0000-0000-0000-000000000011"));
    private static readonly NodeGenerationId OtherGeneration = new(
        Guid.Parse("00000000-0000-0000-0000-000000000012"));
    private static readonly Guid CandidateIdValue =
        Guid.Parse("00000000-0000-0000-0000-000000000021");
    private static readonly Guid ResultIdValue =
        Guid.Parse("00000000-0000-0000-0000-000000000020");
    private const string Hash =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static DelegationArtifactReference CreateArtifact(
        string artifactId = "artifact-1",
        DelegationId? delegationId = null,
        StructuralNodeReference? node = null,
        NodeGenerationId? generation = null,
        string provider = "provider",
        string repository = "repository",
        string location = "artifact-location",
        int schemaVersion = 1) => new(
        delegationId ?? DelegationIdValue,
        node ?? Node,
        generation ?? Generation,
        provider,
        repository,
        artifactId,
        "application/json",
        schemaVersion,
        location,
        ArtifactContentIdentity.Sha256Bytes(Hash));

    private static CandidateRevisionReference CreateCandidate(
        IReadOnlyList<DelegationArtifactReference>? artifacts = null,
        DelegationId? delegationId = null,
        int revision = 1,
        string artifactId = "artifact-1")
    {
        var owner = delegationId ?? DelegationIdValue;
        return new CandidateRevisionReference(
            owner,
            Node,
            Generation,
            new CandidateId(CandidateIdValue),
            revision,
            ArtifactContentIdentity.Sha256Bytes(Hash),
            artifacts ?? [CreateArtifact(artifactId, owner)]);
    }
}
