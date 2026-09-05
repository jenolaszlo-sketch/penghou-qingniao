using FluentAssertions;

namespace Penghou.Qingniao.Tests;

public sealed class SimingSemanticFingerprintVerifierTests
{
    [Fact]
    public void Compute_UsesTheFrozenSimingV2SemanticEnvelope()
    {
        var verifier = new SimingExternalOperationSemanticFingerprintVerifier();

        var fingerprint = verifier.Compute(CreateEnvelope());

        fingerprint.Should().Be("928ce8f6f1a83f33777b4f291f9643ad60c40c23c51953b3339d925abc4f876c");
    }

    [Fact]
    public void Matches_VerifiesIdentityAndExactSemanticEnvelope()
    {
        var verifier = new SimingExternalOperationSemanticFingerprintVerifier();
        var envelope = CreateEnvelope();
        var identity = CreateIdentity(verifier.Compute(envelope));

        verifier.Matches(identity, envelope).Should().BeTrue();

        var changed = CreateEnvelope(capability: "review-code");
        verifier.Matches(identity, changed).Should().BeFalse();

        var otherDelegation = new ExternalOperationSemanticInputEnvelope(
            OtherDelegationId,
            envelope.Agent,
            envelope.Capability,
            envelope.InputArtifacts.Select(artifact => new DelegationArtifactReference(
                OtherDelegationId,
                artifact.StructuralNode,
                artifact.NodeGeneration,
                artifact.Provider,
                artifact.Repository,
                artifact.ArtifactId,
                artifact.Kind,
                artifact.SchemaVersion,
                artifact.Location,
                artifact.ContentIdentity)).ToArray(),
            envelope.Budget,
            envelope.Deadline);

        verifier.Matches(identity, otherDelegation).Should().BeFalse();
    }

    [Fact]
    public void Compute_ChangesForEverySemanticInputAndPreservesArtifactOrder()
    {
        var verifier = new SimingExternalOperationSemanticFingerprintVerifier();
        var artifacts = new[] { CreateArtifact("artifact-1"), CreateArtifact("artifact-2") };
        var baseline = CreateEnvelope(artifacts: artifacts);
        var baselineHash = verifier.Compute(baseline);

        verifier.Compute(CreateEnvelope(capability: "review-code", artifacts: artifacts))
            .Should().NotBe(baselineHash);
        verifier.Compute(CreateEnvelope(
                agent: new ExternalAgentReference("process", "agent-1", "process-v1"),
                artifacts: artifacts))
            .Should().NotBe(baselineHash);
        verifier.Compute(CreateEnvelope(
                artifacts: [CreateArtifact("artifact-3"), CreateArtifact("artifact-2")]))
            .Should().NotBe(baselineHash);
        verifier.Compute(CreateEnvelope(
                artifacts: [CreateArtifact("artifact-1", OtherHash), CreateArtifact("artifact-2")]))
            .Should().NotBe(baselineHash);
        verifier.Compute(CreateEnvelope(
                budget: new ExternalOperationBudgetHint(maximumTokens: 21),
                artifacts: artifacts))
            .Should().NotBe(baselineHash);
        verifier.Compute(CreateEnvelope(deadline: At(21), artifacts: artifacts))
            .Should().NotBe(baselineHash);

        var noHints = new ExternalOperationSemanticInputEnvelope(
            DelegationIdValue,
            baseline.Agent,
            baseline.Capability,
            baseline.InputArtifacts,
            budget: null,
            deadline: null);
        verifier.Compute(noHints).Should().NotBe(baselineHash);

        var reordered = CreateEnvelope(
            artifacts: [CreateArtifact("artifact-2"), CreateArtifact("artifact-1")]);
        verifier.Compute(reordered).Should().NotBe(baselineHash);
    }

    [Fact]
    public void Compute_NormalizesGuidAndDeadlineRepresentations()
    {
        var verifier = new SimingExternalOperationSemanticFingerprintVerifier();
        var utc = CreateEnvelope(deadline: DateTimeOffset.Parse("2026-01-01T00:00:20Z"));
        var offset = CreateEnvelope(deadline: DateTimeOffset.Parse("2026-01-01T08:00:20+08:00"));

        verifier.Compute(utc).Should().Be(verifier.Compute(offset));
    }

    [Fact]
    public void StartRequest_VerifiesThroughTheSimingAdapter()
    {
        var verifier = new SimingExternalOperationSemanticFingerprintVerifier();
        var envelope = CreateEnvelope();
        var identity = CreateIdentity(verifier.Compute(envelope));
        var request = new ExternalOperationStartRequest(
            identity,
            new ExternalOperationCorrelation(
                DelegationIdValue,
                Workflow,
                Node,
                Generation,
                "attempt-1",
                envelope.Agent),
            envelope.Capability,
            envelope.InputArtifacts,
            envelope.Budget,
            envelope.Deadline);

        request.VerifySemanticFingerprint(verifier);
    }

    private static ExternalOperationStartIdentity CreateIdentity(string fingerprint) => new(
        DelegationIdValue,
        Workflow,
        Node,
        Generation,
        "attempt-1",
        "start-key-1",
        fingerprint);

    private static ExternalOperationSemanticInputEnvelope CreateEnvelope(
        string capability = "implement-code",
        ExternalAgentReference? agent = null,
        IReadOnlyList<DelegationArtifactReference>? artifacts = null,
        ExternalOperationBudgetHint? budget = null,
        DateTimeOffset? deadline = null) => new(
            DelegationIdValue,
            agent ?? new ExternalAgentReference("a2a", "agent-1", "a2a-0.3"),
            capability,
            artifacts ?? [CreateArtifact()],
            budget ?? new ExternalOperationBudgetHint(maximumTokens: 20, maximumDuration: TimeSpan.FromTicks(123_456_789)),
            deadline ?? At(20));

    private static DelegationArtifactReference CreateArtifact(
        string artifactId = "artifact-1",
        string contentHash = Hash) => new(
        DelegationIdValue,
        Node,
        Generation,
        "provider",
        "repository",
        artifactId,
        "input",
        1,
        $"artifact-location/{artifactId}",
        ArtifactContentIdentity.Sha256Bytes(contentHash));

    private static DateTimeOffset At(int seconds) =>
        DateTimeOffset.Parse($"2026-01-01T00:00:{seconds:00}Z");

    private static readonly DelegationId DelegationIdValue =
        new(Guid.Parse("00000000-0000-0000-0000-000000000001"));
    private static readonly DelegationId OtherDelegationId =
        new(Guid.Parse("00000000-0000-0000-0000-000000000002"));
    private static readonly WorkflowRunExecutionReference Workflow =
        new("zhinu", "run-1", "epoch-1");
    private static readonly StructuralNodeReference Node = new("implement");
    private static readonly NodeGenerationId Generation =
        new(Guid.Parse("00000000-0000-0000-0000-000000000011"));
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string OtherHash = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";
}
