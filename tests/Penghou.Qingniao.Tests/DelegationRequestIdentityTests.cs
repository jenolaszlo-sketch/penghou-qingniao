using System.Globalization;
using FluentAssertions;

namespace Penghou.Qingniao.Tests;

public sealed class DelegationRequestIdentityTests
{
    [Fact]
    public void Request_collections_are_snapshotted_at_construction()
    {
        var criteria = new List<string> { "first" };
        var constraints = new List<string> { "constraint" };
        var request = CreateRequest(acceptanceCriteria: criteria, constraints: constraints);

        criteria[0] = "changed";
        criteria.Add("later");
        constraints.Clear();

        request.AcceptanceCriteria.Should().Equal("first");
        request.Constraints.Should().Equal("constraint");
    }

    [Fact]
    public void Equivalent_newlines_and_outer_whitespace_have_the_same_identity()
    {
        var first = CreateRequest(objective: "  line one\r\nline two  ");
        var second = CreateRequest(objective: "line one\nline two");

        DelegationRequestIdentity.Compute(first).Should().Be(DelegationRequestIdentity.Compute(second));
        DelegationRequestIdentity.Canonicalize(first).Should().Be(DelegationRequestIdentity.Canonicalize(second));
    }

    [Fact]
    public void Fingerprint_is_stable_across_current_culture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            var request = CreateRequest();
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            var invariant = DelegationRequestIdentity.Compute(request);

            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = new CultureInfo("tr-TR");
            DelegationRequestIdentity.Compute(request).Should().Be(invariant);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void Fingerprint_has_an_explicit_version_and_canonical_payload()
    {
        var request = CreateRequest();

        var fingerprint = DelegationRequestIdentity.Compute(request);

        fingerprint.Version.Should().Be(DelegationRequestFingerprint.CurrentVersion);
        fingerprint.Hash.Should().Be("f90746d5cedc6189cc1160fc8003d472e73aeeceffdb27a9e2bebddc353a4682");
        DelegationRequestIdentity.Canonicalize(request).Should().Be(
            "{\"acceptanceCriteria\":[\"Criteria\"],\"budget\":{\"maximumDurationTicks\":120000000,\"maximumParallelWorkers\":2,\"maximumRetries\":1,\"maximumWorkerCalls\":8},\"constraints\":[\"Constraint\"],\"objective\":\"Do the work\",\"strategy\":0,\"workspace\":{\"identifier\":\"project\",\"revision\":\"revision\",\"provider\":\"local\"}}");
    }

    [Fact]
    public void Request_key_and_caller_scope_are_not_content_fields()
    {
        var first = CreateRequest(requestKey: "one");
        var second = CreateRequest(requestKey: "two");

        DelegationRequestIdentity.Compute(first).Should().Be(DelegationRequestIdentity.Compute(second));
        DelegationRequestIdentity.Canonicalize(first).Should().NotContain("one");
    }

    [Fact]
    public void Plan_bound_identity_uses_v2_and_includes_the_plan_revision()
    {
        var first = CreateRequest(planRevision: WorkflowPlanRevisionReference.BuiltInPreset("Implement", "1"));
        var second = CreateRequest(planRevision: WorkflowPlanRevisionReference.BuiltInPreset("Implement", "2"));

        var fingerprint = DelegationRequestIdentity.Compute(first);

        fingerprint.Version.Should().Be(DelegationRequestFingerprint.PlanBoundVersion);
        fingerprint.Hash.Should().Be("979d2c7765b102e136b42a31085066c65d643f6467a9d6f9add57a2dad2c3c42");
        DelegationRequestIdentity.Compute(first).Should().Be(fingerprint);
        DelegationRequestIdentity.Compute(second).Should().NotBe(fingerprint);
        DelegationRequestIdentity.Canonicalize(first).Should().Contain("Implement");
    }

    [Fact]
    public void Verified_fuwen_plan_requires_a_canonical_sha256_fingerprint()
    {
        var plan = WorkflowPlanRevisionReference.FuwenDefinition(
            "definition-1",
            "revision-1",
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
        var request = CreateRequest(planRevision: plan);

        DelegationRequestIdentity.Compute(request).Version
            .Should().Be(DelegationRequestFingerprint.PlanBoundVersion);

        var invalid = () => WorkflowPlanRevisionReference.FuwenDefinition(
            "definition-1",
            "revision-1",
            "0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef");
        invalid.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Planless_fingerprint_uses_the_historical_v1_contract()
    {
        var request = CreateRequest();

        DelegationRequestIdentity.Compute(request).Version.Should().Be("v1");
    }

    [Fact]
    public void Fingerprint_value_validates_version_hash_and_default()
    {
        var valid = new DelegationRequestFingerprint("future-v3", new string('a', 64));
        valid.Validate();

        var invalid = new Action[]
        {
            () => default(DelegationRequestFingerprint).Validate(),
            () => new DelegationRequestFingerprint(" ", new string('a', 64)),
            () => new DelegationRequestFingerprint("v1", new string('A', 64)),
            () => new DelegationRequestFingerprint("v1", "short"),
            () => new DelegationRequestFingerprint("v\n1", new string('a', 64)),
            () => new DelegationRequestFingerprint("V1", new string('a', 64)),
            () => new DelegationRequestFingerprint("é1", new string('a', 64)),
            () => new DelegationRequestFingerprint(".v1", new string('a', 64)),
        };

        foreach (var action in invalid)
        {
            action.Should().Throw<ArgumentException>();
        }
    }

    [Theory]
    [InlineData(" request-1", "local", "project", "revision")]
    [InlineData("request-1", "local ", "project", "revision")]
    [InlineData("request-1", "local", "project\r\n", "revision")]
    public void Opaque_identity_fields_must_already_be_canonical(
        string requestKey,
        string provider,
        string identifier,
        string revision)
    {
        var act = () => DelegationRequestIdentity.Compute(new DelegationRequest(
            requestKey,
            "Do the work",
            new WorkspaceReference(provider, identifier, revision),
            ["Criteria"],
            ["Constraint"],
            new DelegationBudget(MaximumDuration: TimeSpan.FromSeconds(12))));

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(" caller")]
    [InlineData("caller ")]
    [InlineData("c\r\naller")]
    public void Caller_scope_rejects_noncanonical_principal_identifiers(string identifier)
    {
        var act = () => new DelegationCallerScope(identifier);

        act.Should().Throw<ArgumentException>().WithParameterName("identifier");
    }

    [Fact]
    public void Result_collections_are_snapshotted_at_construction()
    {
        var delegationId = DelegationId.New();
        var changedFiles = new List<string> { "a.cs" };
        var commands = new List<string> { "dotnet test" };
        var artifacts = new List<DelegationArtifactReference>
        {
            new(
                delegationId,
                new StructuralNodeReference("result"),
                new NodeGenerationId(Guid.NewGuid()),
                "provider",
                "repository",
                "artifact-1",
                "result",
                1,
                "artifact-location",
                ArtifactContentIdentity.Sha256Bytes("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")),
        };
        var concerns = new List<string> { "none" };
        var evidence = new DelegationEvidence(changedFiles, commands, 1, 0, true, 0);
        var result = new DelegationResult(
            delegationId,
            DelegationState.Completed,
            "done",
            evidence,
            artifacts,
            concerns,
            DateTimeOffset.UtcNow);

        changedFiles.Add("b.cs");
        commands.Clear();
        artifacts.Clear();
        concerns[0] = "changed";

        result.Evidence.ChangedFiles.Should().Equal("a.cs");
        result.Evidence.Commands.Should().Equal("dotnet test");
        result.Artifacts.Should().ContainSingle();
        result.UnresolvedConcerns.Should().Equal("none");
    }

    [Fact]
    public void Progress_and_evidence_collections_are_snapshotted_at_construction()
    {
        var current = new List<string> { "current" };
        var completed = new List<string> { "completed" };
        var files = new List<string> { "a.cs" };
        var commands = new List<string> { "dotnet test" };
        var progress = new DelegationProgress(
            DelegationId.New(), DelegationState.Running, 1, current, completed, 1, 0, DateTimeOffset.UtcNow);
        var evidence = new DelegationEvidence(files, commands, 1, 0, true, 0);

        current[0] = "changed";
        completed.Clear();
        files.Clear();
        commands[0] = "changed";

        progress.CurrentSteps.Should().Equal("current");
        progress.CompletedSteps.Should().Equal("completed");
        evidence.ChangedFiles.Should().Equal("a.cs");
        evidence.Commands.Should().Equal("dotnet test");
    }

    [Fact]
    public async Task Same_key_and_content_reuses_one_acceptance()
    {
        var registry = new InMemoryDelegationAcceptanceRegistry();
        var caller = new DelegationCallerScope("host-user");
        var request = CreateRequest();
        var testToken = TestContext.Current.CancellationToken;

        var first = await registry.AcceptAsync(caller, request, testToken);
        var second = await registry.AcceptAsync(caller, request, testToken);

        first.IsNew.Should().BeTrue();
        second.IsNew.Should().BeFalse();
        second.DelegationId.Should().Be(first.DelegationId);
        second.Fingerprint.Should().Be(first.Fingerprint);
    }

    [Fact]
    public async Task Same_key_is_isolated_between_caller_scopes()
    {
        var registry = new InMemoryDelegationAcceptanceRegistry();
        var request = CreateRequest();
        var testToken = TestContext.Current.CancellationToken;

        var first = await registry.AcceptAsync(new DelegationCallerScope("caller-a"), request, testToken);
        var second = await registry.AcceptAsync(new DelegationCallerScope("caller-b"), request, testToken);

        first.IsNew.Should().BeTrue();
        second.IsNew.Should().BeTrue();
        second.DelegationId.Should().NotBe(first.DelegationId);
    }

    [Fact]
    public async Task Reusing_a_key_for_different_content_is_rejected_before_acceptance()
    {
        var registry = new InMemoryDelegationAcceptanceRegistry();
        var caller = new DelegationCallerScope("host-user");
        var testToken = TestContext.Current.CancellationToken;
        await registry.AcceptAsync(caller, CreateRequest(objective: "first objective"), testToken);

        var act = () => registry.AcceptAsync(caller, CreateRequest(objective: "second objective"), testToken).AsTask();

        (await act.Should().ThrowAsync<DelegationRequestKeyConflictException>())
            .Which.RequestKey.Should().Be("request-1");
    }

    [Fact]
    public async Task A_plan_change_is_a_conflict_under_the_same_request_key()
    {
        var registry = new InMemoryDelegationAcceptanceRegistry();
        var caller = new DelegationCallerScope("host-user");
        var first = CreateRequest(planRevision: WorkflowPlanRevisionReference.BuiltInPreset("Implement", "1"));
        var changed = CreateRequest(planRevision: WorkflowPlanRevisionReference.BuiltInPreset("Implement", "2"));

        var accepted = await registry.AcceptAsync(caller, first, TestContext.Current.CancellationToken);
        var act = () => registry.AcceptAsync(caller, changed, TestContext.Current.CancellationToken).AsTask();

        (await act.Should().ThrowAsync<DelegationRequestKeyConflictException>())
            .Which.SuppliedFingerprint.Version.Should().Be(DelegationRequestFingerprint.PlanBoundVersion);
        accepted.Fingerprint.Version.Should().Be(DelegationRequestFingerprint.PlanBoundVersion);
    }

    [Fact]
    public async Task Concurrent_duplicate_acceptance_creates_one_delegation()
    {
        var registry = new InMemoryDelegationAcceptanceRegistry();
        var caller = new DelegationCallerScope("host-user");
        var request = CreateRequest();
        var results = new DelegationAcceptance[64];
        var testToken = TestContext.Current.CancellationToken;

        await Task.WhenAll(Enumerable.Range(0, results.Length).Select(async index =>
        {
            await Task.Yield();
            results[index] = await registry.AcceptAsync(caller, request, testToken);
        }));

        results.Select(result => result.DelegationId).Distinct().Should().ContainSingle();
        results.Count(result => result.IsNew).Should().Be(1);
    }

    [Fact]
    public async Task Cancelled_acceptance_does_not_create_an_entry()
    {
        var registry = new InMemoryDelegationAcceptanceRegistry();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var act = () => registry.AcceptAsync(
            new DelegationCallerScope("host-user"), CreateRequest(), cancellation.Token).AsTask();

        await act.Should().ThrowAsync<OperationCanceledException>();
        var accepted = await registry.AcceptAsync(
            new DelegationCallerScope("host-user"), CreateRequest(), TestContext.Current.CancellationToken);
        accepted.IsNew.Should().BeTrue();
    }

    [Fact]
    public void Identity_contracts_reject_default_values()
    {
        var actions = new Action[]
        {
            () => default(HongxianSessionReference).Validate(),
            () => default(StructuralNodeReference).Validate(),
            () => default(NodeGenerationId).Validate(),
            () => default(SupervisorCheckpointId).Validate(),
            () => new WorkflowPlanRevisionReference((WorkflowPlanReferenceKind)99, "plan", "revision", null),
        };

        foreach (var action in actions)
        {
            action.Should().Throw<ArgumentException>();
        }
    }

    [Fact]
    public void Generation_rules_preserve_attempt_and_run_boundaries()
    {
        var generation = new NodeGenerationId(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var anotherGeneration = new NodeGenerationId(Guid.Parse("00000000-0000-0000-0000-000000000002"));
        var firstRun = new WorkflowRunExecutionReference("zhinu", "run-1", "epoch-1");
        var secondRun = new WorkflowRunExecutionReference("zhinu", "run-2", "epoch-2");

        ExecutionIdentityRules.EnsureRetryOrReconnectSameGeneration(generation, generation);
        ExecutionIdentityRules.EnsureSemanticReexecutionNewGeneration(generation, anotherGeneration);
        ExecutionIdentityRules.EnsureReopenedWorkNewRunAndEpoch(firstRun, secondRun);

        var retry = () => ExecutionIdentityRules.EnsureRetryOrReconnectSameGeneration(generation, anotherGeneration);
        var reexecution = () => ExecutionIdentityRules.EnsureSemanticReexecutionNewGeneration(generation, generation);
        var reopen = () => ExecutionIdentityRules.EnsureReopenedWorkNewRunAndEpoch(firstRun, firstRun);

        retry.Should().Throw<InvalidOperationException>();
        reexecution.Should().Throw<InvalidOperationException>();
        reopen.Should().Throw<InvalidOperationException>();
    }

    private static DelegationRequest CreateRequest(
        string requestKey = "request-1",
        string objective = "Do the work",
        IReadOnlyList<string>? acceptanceCriteria = null,
        IReadOnlyList<string>? constraints = null,
        WorkflowPlanRevisionReference? planRevision = null) => new(
        requestKey,
        objective,
        new WorkspaceReference("local", "project", "revision"),
        acceptanceCriteria ?? ["Criteria"],
        constraints ?? ["Constraint"],
        new DelegationBudget(MaximumDuration: TimeSpan.FromSeconds(12)),
        planRevision: planRevision);
}
