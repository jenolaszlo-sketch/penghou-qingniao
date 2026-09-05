using FluentAssertions;

namespace Penghou.Qingniao.Tests;

public sealed class SupervisorContextTests
{
    [Fact]
    public void Request_requires_explicit_facets_and_bounded_limits()
    {
        var valid = new SupervisorContextRequest(new DelegationId(Id(1)), new SupervisorCheckpointId(Id(10)), 2, SupervisorContextFacet.Status, new SupervisorContextLimits(4, 1_024));
        valid.RequestedFacets.Should().Be(SupervisorContextFacet.Status);

        var none = () => new SupervisorContextRequest(new DelegationId(Id(1)), new SupervisorCheckpointId(Id(10)), 2, SupervisorContextFacet.None, new SupervisorContextLimits(4, 1_024));
        var tooMany = () => new SupervisorContextLimits(129, 1_024);
        var noBytes = () => new SupervisorContextLimits(4, 0);
        none.Should().Throw<ArgumentOutOfRangeException>();
        tooMany.Should().Throw<ArgumentOutOfRangeException>();
        noBytes.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Limits_count_utf8_bytes_not_characters()
    {
        var item = new SupervisorContextItem(SupervisorContextFacet.Summary, "summary", "é");
        item.Utf8ByteCount.Should().Be(2);
        var act = () => CreatePackage(items: [item], limits: new SupervisorContextLimits(4, 1));
        act.Should().Throw<ArgumentException>().WithParameterName("appliedLimits");
    }

    [Fact]
    public void Package_snapshots_collections_and_reports_truncation_or_omission()
    {
        var items = new List<SupervisorContextItem> { new(SupervisorContextFacet.Status, "state", "waiting") };
        var artifacts = new List<DelegationArtifactReference>();
        var outcomes = new List<ContextFacetOutcome> { new(SupervisorContextFacet.Summary, ContextFacetAvailability.Truncated, 0, "limit reached") };
        var package = CreatePackage(items, artifacts, facetOutcomes: [new ContextFacetOutcome(SupervisorContextFacet.Status, ContextFacetAvailability.Included, 1), outcomes[0]], requestedFacets: SupervisorContextFacet.Status | SupervisorContextFacet.Summary);
        items.Add(new SupervisorContextItem(SupervisorContextFacet.Status, "later", "hidden")); artifacts.Add(CreateArtifact()); outcomes.Add(new ContextFacetOutcome(SupervisorContextFacet.Correlations, ContextFacetAvailability.Omitted, 0, "not requested"));
        package.Items.Should().ContainSingle();
        package.Artifacts.Should().BeEmpty();
        package.FacetOutcomes.Should().HaveCount(2);
        package.FacetOutcomes[1].Availability.Should().Be(ContextFacetAvailability.Truncated);
    }

    [Fact]
    public void Request_and_package_must_bind_to_exact_waiting_fence()
    {
        var progress = WaitingProgress();
        var request = new SupervisorContextRequest(progress.DelegationId, progress.Checkpoint!.CheckpointId, progress.Revision, SupervisorContextFacet.Status, new SupervisorContextLimits(4, 1_024));
        CreatePackage(limits: new SupervisorContextLimits(4, 1_024)).ValidateAgainst(request, progress);
        var wrongRevision = new SupervisorContextRequest(progress.DelegationId, progress.Checkpoint.CheckpointId, 3, SupervisorContextFacet.Status, new SupervisorContextLimits(4, 1_024));
        var act = () => CreatePackage().ValidateAgainst(wrongRevision, progress);
        act.Should().Throw<ArgumentException>();
        var wrongCheckpoint = new SupervisorContextRequest(progress.DelegationId, new SupervisorCheckpointId(Id(12)), progress.Revision, SupervisorContextFacet.Status, new SupervisorContextLimits(4, 1_024));
        var wrongDelegation = new SupervisorContextRequest(new DelegationId(Id(2)), progress.Checkpoint.CheckpointId, progress.Revision, SupervisorContextFacet.Status, new SupervisorContextLimits(4, 1_024));
        var checkpointAct = () => wrongCheckpoint.ValidateAgainst(progress);
        var delegationAct = () => wrongDelegation.ValidateAgainst(progress);
        checkpointAct.Should().Throw<ArgumentException>();
        delegationAct.Should().Throw<ArgumentException>();
        var notWaiting = new DelegationProgress(progress.DelegationId, DelegationState.Running, progress.Revision, [], [], 0, 0, progress.UpdatedAt);
        var requestAct = () => request.ValidateAgainst(notWaiting);
        requestAct.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Optional_primitive_provenance_is_allowed_but_malformed_references_are_not()
    {
        var package = CreatePackage(requestedFacets: SupervisorContextFacet.PrimitiveReferences, primitiveReferences: [ContextProvenanceReference.CangjieSnapshot("snapshot-1", Hash)], facetOutcomes: [new ContextFacetOutcome(SupervisorContextFacet.PrimitiveReferences, ContextFacetAvailability.Included, 1)]);
        package.PrimitiveReferences.Should().ContainSingle();
        var badHash = () => ContextProvenanceReference.HetuIndexPublication("repository", "index-run", "ABC");
        var badIdentity = () => new ContextProvenanceReference("hetu", "index-run", "bad\nidentifier", "revision", Hash);
        badHash.Should().Throw<ArgumentException>();
        badIdentity.Should().Throw<ArgumentException>();
        CreatePackage().Correlations.Should().BeEmpty();
    }

    [Fact]
    public void Package_requires_exact_outcomes_and_rejects_unrequested_content()
    {
        var progress = WaitingProgress();
        var request = new SupervisorContextRequest(progress.DelegationId, progress.Checkpoint!.CheckpointId, progress.Revision, SupervisorContextFacet.Status | SupervisorContextFacet.Summary | SupervisorContextFacet.Artifacts | SupervisorContextFacet.Correlations | SupervisorContextFacet.PrimitiveReferences, new SupervisorContextLimits(16, 1_024));
        var valid = CreatePackage(
            items: [new SupervisorContextItem(SupervisorContextFacet.Status, "state", "waiting"), new SupervisorContextItem(SupervisorContextFacet.Summary, "summary", "ready")],
            artifacts: [CreateArtifact()],
            correlations: [new ContextCorrelationReference("hongxian", "session", "session-1")],
            primitiveReferences: [ContextProvenanceReference.CangjieSnapshot("snapshot-1")],
            facetOutcomes:
            [
                new ContextFacetOutcome(SupervisorContextFacet.Status, ContextFacetAvailability.Included, 1),
                new ContextFacetOutcome(SupervisorContextFacet.Summary, ContextFacetAvailability.Included, 1),
                new ContextFacetOutcome(SupervisorContextFacet.Artifacts, ContextFacetAvailability.Included, 1),
                new ContextFacetOutcome(SupervisorContextFacet.Correlations, ContextFacetAvailability.Included, 1),
                new ContextFacetOutcome(SupervisorContextFacet.PrimitiveReferences, ContextFacetAvailability.Included, 1),
            ],
            requestedFacets: SupervisorContextFacet.Status | SupervisorContextFacet.Summary | SupervisorContextFacet.Artifacts | SupervisorContextFacet.Correlations | SupervisorContextFacet.PrimitiveReferences);
        valid.ValidateAgainst(request, progress);

        var duplicateAct = () => CreatePackage(facetOutcomes: [new ContextFacetOutcome(SupervisorContextFacet.Status, ContextFacetAvailability.Included, 0), new ContextFacetOutcome(SupervisorContextFacet.Status, ContextFacetAvailability.Included, 0)]);
        duplicateAct.Should().Throw<ArgumentException>();

        var unrequestedAct = () => CreatePackage(items: [new SupervisorContextItem(SupervisorContextFacet.Summary, "summary", "hidden")]);
        unrequestedAct.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Package_rejects_artifacts_owned_by_another_delegation_at_construction()
    {
        var foreignArtifact = new DelegationArtifactReference(
            new DelegationId(Id(2)),
            new StructuralNodeReference("node"),
            new NodeGenerationId(Id(11)),
            "provider",
            "repository",
            "foreign-artifact",
            "text",
            1,
            "artifact-location",
            ArtifactContentIdentity.Sha256Bytes(Hash));

        var act = () => CreatePackage(
            artifacts: [foreignArtifact],
            requestedFacets: SupervisorContextFacet.Status | SupervisorContextFacet.Artifacts,
            facetOutcomes:
            [
                new ContextFacetOutcome(SupervisorContextFacet.Status, ContextFacetAvailability.Included, 0),
                new ContextFacetOutcome(SupervisorContextFacet.Artifacts, ContextFacetAvailability.Included, 1),
            ]);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Provider_boundary_is_async_authorized_and_cancellation_aware()
    {
        var provider = new FakeContextProvider();
        var progress = WaitingProgress();
        var request = new SupervisorContextRequest(progress.DelegationId, progress.Checkpoint!.CheckpointId, progress.Revision, SupervisorContextFacet.Status, new SupervisorContextLimits(4, 1_024));
        var result = await provider.GetAsync(new SupervisorIdentity("authority", "subject"), request, TestContext.Current.CancellationToken);
        result.ValidateAgainst(request, progress);
        provider.SeenSupervisor!.Subject.Should().Be("subject");
        var staleRequest = new SupervisorContextRequest(progress.DelegationId, progress.Checkpoint.CheckpointId, 3, SupervisorContextFacet.Status, new SupervisorContextLimits(4, 1_024));
        var stale = () => provider.GetAsync(new SupervisorIdentity("authority", "subject"), staleRequest, TestContext.Current.CancellationToken).AsTask();
        await stale.Should().ThrowAsync<ArgumentException>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var act = () => provider.GetAsync(new SupervisorIdentity("authority", "subject"), request, cancellation.Token).AsTask();
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static Guid Id(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:D12}");
    private static SupervisorContextPackage CreatePackage(
        IReadOnlyList<SupervisorContextItem>? items = null,
        IReadOnlyList<DelegationArtifactReference>? artifacts = null,
        IReadOnlyList<ContextCorrelationReference>? correlations = null,
        IReadOnlyList<ContextProvenanceReference>? primitiveReferences = null,
        IReadOnlyList<ContextFacetOutcome>? facetOutcomes = null,
        SupervisorContextLimits? limits = null,
        SupervisorContextFacet requestedFacets = SupervisorContextFacet.Status)
        => new(
            new DelegationId(Id(1)),
            new SupervisorCheckpointId(Id(10)),
            2,
            requestedFacets,
            limits ?? new SupervisorContextLimits(16, 1_024),
            items ?? [],
            artifacts ?? [],
            correlations ?? [],
            primitiveReferences ?? [],
            facetOutcomes ?? [new ContextFacetOutcome(SupervisorContextFacet.Status, ContextFacetAvailability.Included, items?.Count ?? 0)]);

    private static DelegationArtifactReference CreateArtifact() => new(
        new DelegationId(Id(1)),
        new StructuralNodeReference("node"),
        new NodeGenerationId(Id(11)),
        "provider",
        "repository",
        "artifact-1",
        "text",
        1,
        "artifact-location",
        ArtifactContentIdentity.Sha256Bytes(Hash));

    private static DelegationProgress WaitingProgress() => new(
        new DelegationId(Id(1)),
        DelegationState.WaitingForSupervisor,
        2,
        [],
        [],
        0,
        0,
        DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
        new SupervisorCheckpointDescriptor(
            new SupervisorCheckpointId(Id(10)),
            new HongxianSessionReference("session-1"),
            new DelegationId(Id(1)),
            WorkflowPlanRevisionReference.BuiltInPreset("Implement", "1"),
            new WorkflowRunExecutionReference("zhinu", "run-1", "epoch-1"),
            new StructuralNodeReference("node"),
            new NodeGenerationId(Id(11)),
            2,
            true));

    private sealed class FakeContextProvider : ISupervisorContextProvider
    {
        public SupervisorIdentity? SeenSupervisor { get; private set; }
        private DelegationProgress Progress { get; } = WaitingProgress();
        public ValueTask<SupervisorContextPackage> GetAsync(
            SupervisorIdentity supervisor,
            SupervisorContextRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(supervisor);
            supervisor.Validate();
            request.ValidateAgainst(Progress);
            SeenSupervisor = supervisor;
            return ValueTask.FromResult(CreatePackage(limits: request.Limits));
        }
    }
}
