using FluentAssertions;

namespace Penghou.Qingniao.Tests;

public sealed class WorkflowPlanResolutionTests
{
    [Fact]
    public void Planless_request_resolves_to_the_stable_implement_revision()
    {
        var resolver = new InMemoryWorkflowPlanResolver();

        var request = CreateRequest();
        var resolution = resolver.Resolve(Caller(), request);

        resolution.PlanRevision.Should().Be(
            WorkflowPlanRevisionReference.BuiltInPreset("Implement", "1"));
        resolution.HasBuiltInStructure.Should().BeTrue();
        resolution.BoundRequest!.PlanRevision.Should().Be(resolution.PlanRevision);
        DelegationRequestIdentity.Compute(resolution.BoundRequest).Version.Should().Be(DelegationRequestFingerprint.PlanBoundVersion);
    }

    [Fact]
    public void Northbound_tool_name_is_not_the_plan_identity()
    {
        var resolution = new InMemoryWorkflowPlanResolver().Resolve(Caller(), CreateRequest());

        resolution.PlanRevision.Identifier.Should().Be("Implement");
        resolution.PlanRevision.Identifier.Should().NotBe("delegate");
    }

    [Fact]
    public void Fixed_preset_has_only_the_bounded_fixed_shape()
    {
        var definition = new InMemoryWorkflowPlanResolver().Resolve(Caller(), CreateRequest()).Definition!;

        definition.Identifier.Should().Be("implement-preset");
        definition.Implement.Kind.Should().Be(WorkflowPlanStageKind.Implement);
        definition.SealCandidate.Kind.Should().Be(WorkflowPlanStageKind.SealCandidate);
        definition.InitialVerification.Test.Kind.Should().Be(WorkflowPlanStageKind.Test);
        definition.InitialVerification.Review.Kind.Should().Be(WorkflowPlanStageKind.Review);
        definition.Evaluate.Kind.Should().Be(WorkflowPlanStageKind.Evaluate);
        definition.OptionalFix.MaximumExecutions.Should().Be(1);
        definition.OptionalFix.Fix.Kind.Should().Be(WorkflowPlanStageKind.Fix);
        definition.OptionalFix.Verification.Test.Kind.Should().Be(WorkflowPlanStageKind.Test);
        definition.OptionalFix.Verification.Review.Kind.Should().Be(WorkflowPlanStageKind.Review);
        definition.Result.Kind.Should().Be(WorkflowPlanStageKind.Result);
    }

    [Fact]
    public void Explicit_builtin_revision_resolves_to_the_same_catalog_entry()
    {
        var request = CreateRequest(WorkflowPlanRevisionReference.BuiltInPreset("Implement", "1"));

        var resolution = new InMemoryWorkflowPlanResolver().Resolve(Caller(), request);

        resolution.PlanRevision.Should().Be(InMemoryWorkflowPlanCatalog.ImplementRevision);
        resolution.Definition.Should().NotBeNull();
        DelegationRequestIdentity.Compute(resolution.BoundRequest!).Should().Be(
            DelegationRequestIdentity.Compute(new InMemoryWorkflowPlanResolver().Resolve(Caller(), CreateRequest()).BoundRequest!));
    }

    [Fact]
    public void Builtin_verifier_is_not_called()
    {
        var verifier = new StubVerifier(WorkflowPlanVerificationResult.Verified());

        new InMemoryWorkflowPlanResolver(verifier).Resolve(
            Caller(),
            CreateRequest(WorkflowPlanRevisionReference.BuiltInPreset("Implement", "1")));

        verifier.Seen.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Implement", "2")]
    [InlineData("Other", "1")]
    public void Unknown_builtin_revisions_are_rejected_before_resolution(
        string identifier,
        string revision)
    {
        var request = CreateRequest(WorkflowPlanRevisionReference.BuiltInPreset(identifier, revision));

        var act = () => new InMemoryWorkflowPlanResolver().Resolve(Caller(), request);

        act.Should().Throw<WorkflowPlanResolutionException>()
            .Which.Status.Should().Be(WorkflowPlanVerificationStatus.Unknown);
    }

    [Fact]
    public void Fuwen_reference_requires_an_injected_host_verifier()
    {
        var reference = FuwenReference();

        var act = () => new InMemoryWorkflowPlanResolver().Resolve(Caller(), CreateRequest(reference));

        act.Should().Throw<WorkflowPlanResolutionException>()
            .Which.Status.Should().Be(WorkflowPlanVerificationStatus.Unknown);
    }

    [Fact]
    public void Fuwen_verification_precedes_a_custom_catalog_lookup()
    {
        var catalog = new StubCatalog
        {
            Resolution = new WorkflowPlanResolution(FuwenReference(), ImplementWorkflowPlanDefinition.Create()),
        };

        var act = () => new InMemoryWorkflowPlanResolver(catalog).Resolve(Caller(), CreateRequest(FuwenReference()));

        act.Should().Throw<WorkflowPlanResolutionException>()
            .Which.Status.Should().Be(WorkflowPlanVerificationStatus.Unknown);
        catalog.Called.Should().BeFalse();
    }

    [Theory]
    [InlineData(WorkflowPlanVerificationStatus.Unknown)]
    [InlineData(WorkflowPlanVerificationStatus.Unauthorized)]
    [InlineData(WorkflowPlanVerificationStatus.Stale)]
    [InlineData(WorkflowPlanVerificationStatus.FingerprintMismatch)]
    public void Host_rejection_statuses_are_rejected_before_execution(
        WorkflowPlanVerificationStatus status)
    {
        var verifier = new StubVerifier(new WorkflowPlanVerificationResult(status));
        var resolver = new InMemoryWorkflowPlanResolver(verifier);

        var act = () => resolver.Resolve(Caller(), CreateRequest(FuwenReference()));

        act.Should().Throw<WorkflowPlanResolutionException>()
            .Which.Status.Should().Be(status);
        verifier.Seen.Should().ContainSingle();
        verifier.Seen[0].PlanRevision.Should().Be(FuwenReference());
    }

    [Fact]
    public void Verified_Fuwen_reference_remains_opaque_and_is_not_given_a_built_in_graph()
    {
        var reference = FuwenReference();
        var verifier = new StubVerifier(WorkflowPlanVerificationResult.Verified());

        var request = CreateRequest(reference);
        var caller = Caller();
        var resolution = new InMemoryWorkflowPlanResolver(verifier).Resolve(caller, request);

        resolution.PlanRevision.Should().Be(reference);
        resolution.HasBuiltInStructure.Should().BeFalse();
        resolution.Definition.Should().BeNull();
        resolution.BoundRequest!.PlanRevision.Should().Be(reference);
        verifier.Seen.Should().ContainSingle();
        verifier.Seen[0].PlanRevision.Should().Be(reference);
        verifier.Seen[0].PlanRevision.Should().BeSameAs(reference);
        verifier.Seen[0].Caller.Should().BeSameAs(caller);
        verifier.Seen[0].Workspace.Should().Be(request.Workspace);
    }

    [Fact]
    public void Catalog_does_not_accept_opaque_or_unknown_references()
    {
        var catalog = new InMemoryWorkflowPlanCatalog();
        var unknown = WorkflowPlanRevisionReference.BuiltInPreset("Implement", "2");
        var fuwen = FuwenReference();

        catalog.TryGet(unknown, out var unknownResolution).Should().BeFalse();
        unknownResolution.Should().BeNull();
        catalog.TryGet(fuwen, out var fuwenResolution).Should().BeFalse();
        fuwenResolution.Should().BeNull();
    }

    [Fact]
    public void Malformed_catalog_output_is_rejected()
    {
        var catalog = new StubCatalog
        {
            Resolution = new WorkflowPlanResolution(InMemoryWorkflowPlanCatalog.ImplementRevision, definition: null),
        };

        var act = () => new InMemoryWorkflowPlanResolver(catalog).Resolve(Caller(), CreateRequest());

        act.Should().Throw<WorkflowPlanResolutionException>()
            .Which.Status.Should().Be(WorkflowPlanVerificationStatus.Unknown);
    }

    [Fact]
    public void Duplicate_structural_identifiers_are_rejected_recursively()
    {
        var duplicate = new ImplementWorkflowPlanDefinition(
            "implement",
            new WorkflowPlanAction("implement", WorkflowPlanStageKind.Implement),
            new WorkflowPlanAction("same", WorkflowPlanStageKind.SealCandidate),
            new WorkflowPlanVerificationPair(
                "test-and-review",
                new WorkflowPlanAction("test", WorkflowPlanStageKind.Test),
                new WorkflowPlanAction("review", WorkflowPlanStageKind.Review)),
            new WorkflowPlanAction("evaluate", WorkflowPlanStageKind.Evaluate),
            new WorkflowPlanConditionalFix(
                "optional-fix",
                new WorkflowPlanAction("fix", WorkflowPlanStageKind.Fix),
                new WorkflowPlanVerificationPair(
                    "fix-test-and-review",
                    new WorkflowPlanAction("fix-test", WorkflowPlanStageKind.Test),
                    new WorkflowPlanAction("fix-review", WorkflowPlanStageKind.Review)),
                1),
            new WorkflowPlanAction("result", WorkflowPlanStageKind.Result));
        var catalog = new StubCatalog
        {
            Resolution = new WorkflowPlanResolution(InMemoryWorkflowPlanCatalog.ImplementRevision, duplicate),
        };

        var act = () => new InMemoryWorkflowPlanResolver(catalog).Resolve(Caller(), CreateRequest());

        act.Should().Throw<WorkflowPlanResolutionException>()
            .Which.Status.Should().Be(WorkflowPlanVerificationStatus.Unknown);
    }

    [Fact]
    public void Verification_result_rejects_invalid_statuses_and_verified_rejection_factory()
    {
        var invalid = () => new WorkflowPlanVerificationResult((WorkflowPlanVerificationStatus)99);
        var invalidFactory = () => WorkflowPlanVerificationResult.Rejected(WorkflowPlanVerificationStatus.Verified);

        invalid.Should().Throw<ArgumentOutOfRangeException>();
        invalidFactory.Should().Throw<ArgumentException>();
    }

    private static DelegationCallerScope Caller() => new("caller-1");

    private static DelegationRequest CreateRequest(WorkflowPlanRevisionReference? planRevision = null) => new(
        "request-1",
        "Implement the objective",
        new WorkspaceReference("local", "workspace", "revision"),
        ["The result is correct"],
        [],
        new DelegationBudget(),
        planRevision: planRevision);

    private static WorkflowPlanRevisionReference FuwenReference() =>
        WorkflowPlanRevisionReference.FuwenDefinition(
            "opaque-definition",
            "revision-1",
            new string('a', 64));

    private sealed class StubVerifier(WorkflowPlanVerificationResult result) : IWorkflowPlanHostVerifier
    {
        public List<WorkflowPlanVerificationContext> Seen { get; } = [];

        public WorkflowPlanVerificationResult Verify(WorkflowPlanVerificationContext context)
        {
            Seen.Add(context);
            return result;
        }
    }

    private sealed class StubCatalog : IWorkflowPlanCatalog
    {
        public WorkflowPlanResolution? Resolution { get; init; }
        public bool Called { get; private set; }

        public bool TryGet(WorkflowPlanRevisionReference reference, out WorkflowPlanResolution? resolution)
        {
            Called = true;
            resolution = Resolution;
            return Resolution is not null;
        }
    }
}
