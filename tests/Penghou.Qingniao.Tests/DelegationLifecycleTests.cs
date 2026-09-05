using FluentAssertions;

namespace Penghou.Qingniao.Tests;

public sealed class DelegationLifecycleTests
{
    public static IEnumerable<object[]> StatePairs()
    {
        foreach (var current in Enum.GetValues<DelegationState>())
        {
            foreach (var next in Enum.GetValues<DelegationState>())
            {
                yield return [current, next, IsExpectedTransition(current, next)];
            }
        }
    }

    public static IEnumerable<object[]> IllegalStatePairs()
    {
        foreach (var current in Enum.GetValues<DelegationState>())
        {
            foreach (var next in Enum.GetValues<DelegationState>())
            {
                if (!IsExpectedTransition(current, next))
                {
                    yield return [current, next];
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(StatePairs))]
    public void Transition_matrix_matches_the_fixed_strategy(
        DelegationState current,
        DelegationState next,
        bool expected)
    {
        DelegationLifecycle.CanTransition(current, next).Should().Be(expected);

        var act = () => DelegationLifecycle.EnsureTransition(current, next);
        if (expected)
        {
            act.Should().NotThrow();
        }
        else
        {
            act.Should().Throw<DelegationLifecycleViolationException>();
        }
    }

    [Theory]
    [MemberData(nameof(IllegalStatePairs))]
    public void Illegal_transitions_are_rejected(DelegationState current, DelegationState next)
    {
        var act = () => DelegationLifecycle.EnsureTransition(current, next);

        act.Should().Throw<DelegationLifecycleViolationException>();
    }

    [Theory]
    [InlineData(DelegationState.Completed)]
    [InlineData(DelegationState.Failed)]
    [InlineData(DelegationState.Cancelled)]
    [InlineData(DelegationState.BudgetExceeded)]
    [InlineData(DelegationState.NeedsSupervisor)]
    public void Terminal_states_are_explicit_including_needs_supervisor(DelegationState state)
    {
        DelegationLifecycle.IsTerminal(state).Should().BeTrue();
    }

    [Theory]
    [InlineData(DelegationState.Queued)]
    [InlineData(DelegationState.Running)]
    [InlineData(DelegationState.WaitingForSupervisor)]
    public void Queued_and_running_are_nonterminal(DelegationState state)
    {
        DelegationLifecycle.IsTerminal(state).Should().BeFalse();
    }

    [Fact]
    public void Identical_progress_revision_is_idempotent()
    {
        var first = CreateProgress(DelegationState.Running, revision: 2);
        var same = CreateProgress(DelegationState.Running, revision: 2);

        DelegationLifecycle.ValidateProgress(same, first);
    }

    [Fact]
    public void Nonterminal_same_state_with_a_greater_revision_is_allowed()
    {
        var previous = CreateProgress(DelegationState.Running, revision: 1);
        var next = CreateProgress(DelegationState.Running, revision: 2);

        DelegationLifecycle.ValidateProgress(next, previous);
    }

    [Fact]
    public void Terminal_same_state_with_a_greater_revision_is_rejected()
    {
        var previous = CreateProgress(DelegationState.Completed, revision: 1);
        var next = CreateProgress(DelegationState.Completed, revision: 2);

        var act = () => DelegationLifecycle.ValidateProgress(next, previous);

        act.Should().Throw<DelegationLifecycleViolationException>();
    }

    [Fact]
    public void Exact_terminal_progress_replay_is_allowed()
    {
        var previous = CreateProgress(DelegationState.Completed, revision: 1);
        var replay = CreateProgress(DelegationState.Completed, revision: 1);

        DelegationLifecycle.ValidateProgress(replay, previous);
    }

    [Fact]
    public void Changed_progress_with_same_revision_is_rejected()
    {
        var previous = CreateProgress(DelegationState.Running, revision: 2);
        var changed = CreateProgress(DelegationState.Running, revision: 2, workerCalls: 2);

        var act = () => DelegationLifecycle.ValidateProgress(changed, previous);

        act.Should().Throw<DelegationLifecycleViolationException>();
    }

    [Fact]
    public void Progress_revision_counters_and_timestamp_cannot_regress()
    {
        var previous = CreateProgress(DelegationState.Running, revision: 2, workerCalls: 2, retries: 1);
        var cases = new[]
        {
            CreateProgress(DelegationState.Running, revision: 1, workerCalls: 2, retries: 1),
            CreateProgress(DelegationState.Running, revision: 3, workerCalls: 1, retries: 1),
            CreateProgress(DelegationState.Running, revision: 3, workerCalls: 2, retries: 0),
            CreateProgress(DelegationState.Running, revision: 3, workerCalls: 2, retries: 1, timestamp: previous.UpdatedAt.AddSeconds(-1)),
        };

        foreach (var next in cases)
        {
            var act = () => DelegationLifecycle.ValidateProgress(next, previous);
            act.Should().Throw<DelegationLifecycleViolationException>();
        }
    }

    [Fact]
    public void Progress_constructor_rejects_invalid_revision_counts_and_timestamp()
    {
        var negativeRevision = () => CreateProgress(DelegationState.Queued, revision: -1);
        var negativeWorkerCalls = () => CreateProgress(DelegationState.Queued, workerCalls: -1);
        var negativeRetries = () => CreateProgress(DelegationState.Queued, retries: -1);
        var defaultTimestamp = () => CreateProgress(DelegationState.Queued, timestamp: DateTimeOffset.MinValue);

        negativeRevision.Should().Throw<ArgumentOutOfRangeException>();
        negativeWorkerCalls.Should().Throw<ArgumentOutOfRangeException>();
        negativeRetries.Should().Throw<ArgumentOutOfRangeException>();
        defaultTimestamp.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Progress_state_change_requires_a_legal_transition()
    {
        var previous = CreateProgress(DelegationState.Queued, revision: 1);
        var next = CreateProgress(DelegationState.Completed, revision: 2);

        var act = () => DelegationLifecycle.ValidateProgress(next, previous);

        act.Should().Throw<DelegationLifecycleViolationException>();
    }

    [Fact]
    public void Running_can_wait_and_resume_with_a_checkpoint()
    {
        var delegationId = new DelegationId(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var running = CreateProgress(DelegationState.Running, revision: 1);
        var waiting = CreateProgress(
            DelegationState.WaitingForSupervisor,
            revision: 2,
            delegationId: delegationId,
            checkpoint: CreateCheckpoint(delegationId, expectedRevision: 2));
        var resumed = CreateProgress(DelegationState.Running, revision: 3, delegationId: delegationId);

        DelegationLifecycle.ValidateProgress(waiting, running);
        DelegationLifecycle.ValidateProgress(resumed, waiting);
    }

    [Fact]
    public void Queued_can_enter_a_supervisor_checkpoint()
    {
        var delegationId = new DelegationId(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var queued = CreateProgress(DelegationState.Queued, delegationId: delegationId);
        var waiting = CreateProgress(
            DelegationState.WaitingForSupervisor,
            revision: 2,
            delegationId: delegationId,
            checkpoint: CreateCheckpoint(delegationId, expectedRevision: 2));

        DelegationLifecycle.ValidateProgress(waiting, queued);
    }

    [Fact]
    public void Waiting_updates_may_advance_only_the_observable_revision()
    {
        var delegationId = new DelegationId(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var first = CreateProgress(
            DelegationState.WaitingForSupervisor,
            revision: 2,
            delegationId: delegationId,
            checkpoint: CreateCheckpoint(delegationId, expectedRevision: 2));
        var next = CreateProgress(
            DelegationState.WaitingForSupervisor,
            revision: 3,
            delegationId: delegationId,
            checkpoint: CreateCheckpoint(delegationId, expectedRevision: 3));

        DelegationLifecycle.ValidateProgress(next, first);

        var changedTarget = CreateProgress(
            DelegationState.WaitingForSupervisor,
            revision: 3,
            delegationId: delegationId,
            checkpoint: CreateCheckpoint(
                delegationId,
                expectedRevision: 3,
                checkpointId: new SupervisorCheckpointId(Guid.Parse("00000000-0000-0000-0000-000000000012"))));
        var act = () => DelegationLifecycle.ValidateProgress(changedTarget, first);

        act.Should().Throw<DelegationLifecycleViolationException>();
    }

    [Fact]
    public void Same_revision_waiting_replay_cannot_swap_checkpoint()
    {
        var delegationId = new DelegationId(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var first = CreateProgress(
            DelegationState.WaitingForSupervisor,
            revision: 2,
            delegationId: delegationId,
            checkpoint: CreateCheckpoint(delegationId, expectedRevision: 2));
        var replay = CreateProgress(
            DelegationState.WaitingForSupervisor,
            revision: 2,
            delegationId: delegationId,
            checkpoint: CreateCheckpoint(
                delegationId,
                expectedRevision: 2,
                checkpointId: new SupervisorCheckpointId(Guid.Parse("00000000-0000-0000-0000-000000000012"))));

        var act = () => DelegationLifecycle.ValidateProgress(replay, first);

        act.Should().Throw<DelegationLifecycleViolationException>();
    }

    [Fact]
    public void Waiting_requires_a_consistent_checkpoint_descriptor()
    {
        var delegationId = new DelegationId(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var missing = CreateProgress(DelegationState.WaitingForSupervisor, delegationId: delegationId);
        var future = CreateProgress(
            DelegationState.WaitingForSupervisor,
            revision: 2,
            delegationId: delegationId,
            checkpoint: CreateCheckpoint(delegationId, expectedRevision: 3));
        var stale = CreateProgress(
            DelegationState.WaitingForSupervisor,
            revision: 2,
            delegationId: delegationId,
            checkpoint: CreateCheckpoint(delegationId, expectedRevision: 1));
        var wrongId = CreateProgress(
            DelegationState.WaitingForSupervisor,
            revision: 2,
            delegationId: delegationId,
            checkpoint: CreateCheckpoint(new DelegationId(Guid.Parse("00000000-0000-0000-0000-000000000002")), expectedRevision: 2));

        foreach (var progress in new[] { missing, future, stale, wrongId })
        {
            var act = () => DelegationLifecycle.ValidateProgress(progress);
            act.Should().Throw<DelegationLifecycleViolationException>();
        }
    }

    [Fact]
    public void Nonblocking_checkpoint_descriptor_is_rejected()
    {
        var act = () => new SupervisorCheckpointDescriptor(
            new SupervisorCheckpointId(Guid.Parse("00000000-0000-0000-0000-000000000010")),
            new HongxianSessionReference("session-1"),
            new DelegationId(Guid.Parse("00000000-0000-0000-0000-000000000001")),
            WorkflowPlanRevisionReference.BuiltInPreset("Implement", "1"),
            new WorkflowRunExecutionReference("zhinu", "run-1", "epoch-1"),
            new StructuralNodeReference("implement"),
            new NodeGenerationId(Guid.Parse("00000000-0000-0000-0000-000000000011")),
            1,
            false);

        act.Should().Throw<ArgumentException>().WithParameterName("dependentProgressGated");
    }

    [Fact]
    public void Terminal_result_is_required_only_for_terminal_progress()
    {
        var queued = CreateProgress(DelegationState.Queued);
        var completed = CreateProgress(DelegationState.Completed);
        var result = CreateResult(completed.DelegationId, DelegationState.Completed, completed.UpdatedAt);

        DelegationLifecycle.ValidateResultAvailability(queued, null);
        DelegationLifecycle.ValidateResultAvailability(completed, result);

        var missing = () => DelegationLifecycle.ValidateResultAvailability(completed, null);
        missing.Should().Throw<DelegationLifecycleViolationException>();
    }

    [Fact]
    public void Nonterminal_result_and_mismatched_terminal_result_are_rejected()
    {
        var running = CreateProgress(DelegationState.Running);
        var completed = CreateProgress(DelegationState.Completed);
        var nonterminalResult = CreateResult(running.DelegationId, DelegationState.Running, running.UpdatedAt);
        var wrongState = CreateResult(completed.DelegationId, DelegationState.Failed, completed.UpdatedAt);
        var wrongId = CreateResult(DelegationId.New(), DelegationState.Completed, completed.UpdatedAt);
        var early = CreateResult(completed.DelegationId, DelegationState.Completed, completed.UpdatedAt.AddSeconds(-1));

        foreach (var (progress, result) in new[]
        {
            (running, nonterminalResult),
            (completed, wrongState),
            (completed, wrongId),
            (completed, early),
        })
        {
            var act = () => DelegationLifecycle.ValidateResultAvailability(progress, result);
            act.Should().Throw<DelegationLifecycleViolationException>();
        }
    }

    [Fact]
    public void Result_validation_rejects_invalid_state_summary_counts_and_timestamp()
    {
        var id = DelegationId.New();
        var nonterminal = new DelegationResult(id, DelegationState.Running, "summary", new DelegationEvidence([], [], 0, 0, null, 0), [], [], DateTimeOffset.UtcNow);
        var nonterminalAct = () => DelegationLifecycle.ValidateResult(nonterminal);
        nonterminalAct.Should().Throw<DelegationLifecycleViolationException>();

        var emptySummary = () => new DelegationResult(id, DelegationState.Completed, "", new DelegationEvidence([], [], 0, 0, null, 0), [], [], DateTimeOffset.UtcNow);
        var defaultTimestamp = () => new DelegationResult(id, DelegationState.Completed, "summary", new DelegationEvidence([], [], 0, 0, null, 0), [], [], default);
        var nullEvidence = () => new DelegationResult(id, DelegationState.Completed, "summary", null!, [], [], DateTimeOffset.UtcNow);
        emptySummary.Should().Throw<ArgumentException>();
        defaultTimestamp.Should().Throw<ArgumentException>();
        nullEvidence.Should().Throw<ArgumentNullException>();

        var negativeEvidence = () => new DelegationEvidence([], [], -1, 0, null, 0);
        negativeEvidence.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Progress_constructor_rejects_invalid_identity_state_bounds_steps_and_timestamp()
    {
        var validId = new DelegationId(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var emptyId = () => new DelegationProgress(default, DelegationState.Queued, 0, [], [], 0, 0, DateTimeOffset.UtcNow);
        var unknownState = () => new DelegationProgress(validId, (DelegationState)99, 0, [], [], 0, 0, DateTimeOffset.UtcNow);
        var negativeRevision = () => new DelegationProgress(validId, DelegationState.Queued, -1, [], [], 0, 0, DateTimeOffset.UtcNow);
        var hugeRevision = () => new DelegationProgress(validId, DelegationState.Queued, 1_000_000_001, [], [], 0, 0, DateTimeOffset.UtcNow);
        var negativeCounter = () => new DelegationProgress(validId, DelegationState.Queued, 0, [], [], -1, 0, DateTimeOffset.UtcNow);
        var oversizedSteps = () => new DelegationProgress(validId, DelegationState.Queued, 0, [new string('x', 4_097)], [], 0, 0, DateTimeOffset.UtcNow);
        var defaultTimestamp = () => new DelegationProgress(validId, DelegationState.Queued, 0, [], [], 0, 0, default);

        emptyId.Should().Throw<ArgumentException>();
        unknownState.Should().Throw<ArgumentOutOfRangeException>();
        negativeRevision.Should().Throw<ArgumentOutOfRangeException>();
        hugeRevision.Should().Throw<ArgumentOutOfRangeException>();
        negativeCounter.Should().Throw<ArgumentOutOfRangeException>();
        oversizedSteps.Should().Throw<ArgumentException>();
        defaultTimestamp.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Terminal_result_rejects_cross_delegation_null_duplicate_and_oversized_artifacts()
    {
        var id = new DelegationId(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var other = new DelegationId(Guid.Parse("00000000-0000-0000-0000-000000000002"));
        var artifact = CreateArtifact(id, "artifact-1");
        var crossDelegation = CreateArtifact(other, "artifact-2");

        var wrongOwner = () => new DelegationResult(id, DelegationState.Completed, "summary", new DelegationEvidence([], [], 0, 0, null, 0), [crossDelegation], [], DateTimeOffset.UtcNow);
        var nullArtifact = () => new DelegationResult(id, DelegationState.Completed, "summary", new DelegationEvidence([], [], 0, 0, null, 0), [null!], [], DateTimeOffset.UtcNow);
        var duplicate = () => new DelegationResult(id, DelegationState.Completed, "summary", new DelegationEvidence([], [], 0, 0, null, 0), [artifact, artifact], [], DateTimeOffset.UtcNow);
        var oversized = () => new DelegationResult(id, DelegationState.Completed, "summary", new DelegationEvidence([], [], 0, 0, null, 0), Enumerable.Range(0, 129).Select(index => CreateArtifact(id, $"artifact-{index}")).ToArray(), [], DateTimeOffset.UtcNow);

        wrongOwner.Should().Throw<ArgumentException>();
        nullArtifact.Should().Throw<ArgumentNullException>();
        duplicate.Should().Throw<ArgumentException>();
        oversized.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Legacy_evidence_and_concerns_are_snapshotted_and_bounded()
    {
        var changedFiles = new List<string> { "a.cs" };
        var commands = new List<string> { "dotnet test" };
        var concerns = new List<string> { "needs follow-up" };
        var result = new DelegationResult(
            DelegationId.New(),
            DelegationState.Completed,
            "summary",
            new DelegationEvidence(changedFiles, commands, 1, 0, true, 0),
            [],
            concerns,
            DateTimeOffset.UtcNow);

        changedFiles.Add("later.cs");
        commands.Add("later");
        concerns.Add("later");
        result.Evidence.ChangedFiles.Should().ContainSingle();
        result.Evidence.Commands.Should().ContainSingle();
        result.UnresolvedConcerns.Should().ContainSingle();

        var tooManyFiles = () => new DelegationEvidence(Enumerable.Repeat("a.cs", 129).ToArray(), [], 0, 0, null, 0);
        var oversizedCommand = () => new DelegationEvidence([], [new string('x', 4_097)], 0, 0, null, 0);
        var oversizedConcern = () => new DelegationResult(DelegationId.New(), DelegationState.Completed, "summary", new DelegationEvidence([], [], 0, 0, null, 0), [], [new string('x', 4_097)], DateTimeOffset.UtcNow);
        var duplicateConcern = () => new DelegationResult(DelegationId.New(), DelegationState.Completed, "summary", new DelegationEvidence([], [], 0, 0, null, 0), [], ["same", "same"], DateTimeOffset.UtcNow);

        tooManyFiles.Should().Throw<ArgumentException>();
        oversizedCommand.Should().Throw<ArgumentException>();
        oversizedConcern.Should().Throw<ArgumentException>();
        duplicateConcern.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void First_result_is_authoritative_and_exact_replay_is_idempotent()
    {
        var existing = CreateResult(
            new DelegationId(Guid.Parse("00000000-0000-0000-0000-000000000001")),
            DelegationState.Completed,
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var replay = CreateResult(existing.DelegationId, existing.State, existing.CompletedAt);

        DelegationLifecycle.ValidateResultPublication(existing, replay);
    }

    [Fact]
    public void Result_replacements_with_any_changed_evidence_are_rejected()
    {
        var id = new DelegationId(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var completedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var existing = CreateResult(id, DelegationState.Completed, completedAt);
        var replacements = new[]
        {
            new DelegationResult(id, DelegationState.Completed, "different", existing.Evidence, existing.Artifacts, existing.UnresolvedConcerns, completedAt),
            new DelegationResult(id, DelegationState.Completed, existing.Summary, new DelegationEvidence(["other.cs"], ["dotnet test"], 1, 0, true, 0), existing.Artifacts, existing.UnresolvedConcerns, completedAt),
            new DelegationResult(id, DelegationState.Completed, existing.Summary, existing.Evidence, [new DelegationArtifactReference(id, new StructuralNodeReference("other"), new NodeGenerationId(Guid.Parse("00000000-0000-0000-0000-000000000012")), "provider", "repository", "artifact-2", "other", 1, "artifact-location", ArtifactContentIdentity.Sha256Bytes("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"))], existing.UnresolvedConcerns, completedAt),
            new DelegationResult(id, DelegationState.Completed, existing.Summary, existing.Evidence, existing.Artifacts, ["different"], completedAt),
            new DelegationResult(id, DelegationState.Completed, existing.Summary, existing.Evidence, existing.Artifacts, existing.UnresolvedConcerns, completedAt.AddSeconds(1)),
        };

        foreach (var candidate in replacements)
        {
            var act = () => DelegationLifecycle.ValidateResultPublication(existing, candidate);
            act.Should().Throw<DelegationLifecycleViolationException>();
        }
    }

    [Fact]
    public void Invalid_predecessor_progress_is_rejected_too()
    {
        var act = () => CreateProgress(DelegationState.Running, revision: -1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Unknown_states_are_rejected_consistently()
    {
        var unknown = (DelegationState)99;

        var actions = new Action[]
        {
            () => DelegationLifecycle.IsTerminal(unknown),
            () => DelegationLifecycle.CanTransition(unknown, DelegationState.Queued),
            () => DelegationLifecycle.CanTransition(DelegationState.Queued, unknown),
            () => DelegationLifecycle.EnsureTransition(unknown, DelegationState.Queued),
            () => new DelegationProgress(DelegationId.New(), unknown, 1, [], [], 0, 0, DateTimeOffset.UtcNow),
            () => new DelegationResult(DelegationId.New(), unknown, "summary", new DelegationEvidence([], [], 0, 0, null, 0), [], [], DateTimeOffset.UtcNow),
        };

        foreach (var action in actions)
        {
            action.Should().Throw<ArgumentOutOfRangeException>();
        }
    }

    private static DelegationProgress CreateProgress(
        DelegationState state,
        long revision = 1,
        int workerCalls = 0,
        int retries = 0,
        DateTimeOffset? timestamp = null,
        DelegationId? delegationId = null,
        SupervisorCheckpointDescriptor? checkpoint = null) => new(
        delegationId ?? new DelegationId(Guid.Parse("00000000-0000-0000-0000-000000000001")),
        state,
        revision,
        [],
        [],
        workerCalls,
        retries,
        timestamp ?? DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
        checkpoint);

    private static SupervisorCheckpointDescriptor CreateCheckpoint(
        DelegationId delegationId,
        long expectedRevision,
        SupervisorCheckpointId? checkpointId = null) => new(
        checkpointId ?? new SupervisorCheckpointId(Guid.Parse("00000000-0000-0000-0000-000000000010")),
        new HongxianSessionReference("session-1"),
        delegationId,
        WorkflowPlanRevisionReference.BuiltInPreset("Implement", "1"),
        new WorkflowRunExecutionReference("zhinu", "run-1", "epoch-1"),
        new StructuralNodeReference("implement"),
        new NodeGenerationId(Guid.Parse("00000000-0000-0000-0000-000000000011")),
        expectedRevision,
        true);

    private static DelegationResult CreateResult(DelegationId id, DelegationState state, DateTimeOffset completedAt) => new(
        id,
        state,
        "summary",
        new DelegationEvidence([], [], 1, 0, true, 0),
        [],
        [],
        completedAt);

    private static DelegationArtifactReference CreateArtifact(DelegationId delegationId, string artifactId) => new(
        delegationId,
        new StructuralNodeReference("implement"),
        new NodeGenerationId(Guid.Parse("00000000-0000-0000-0000-000000000011")),
        "provider",
        "repository",
        artifactId,
        "text",
        1,
        "artifact-location",
        ArtifactContentIdentity.Sha256Bytes("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"));

    private static bool IsExpectedTransition(DelegationState current, DelegationState next) =>
        (current, next) switch
        {
            (DelegationState.Queued, DelegationState.Running) => true,
            (DelegationState.Queued, DelegationState.Failed) => true,
            (DelegationState.Queued, DelegationState.Cancelled) => true,
            (DelegationState.Queued, DelegationState.BudgetExceeded) => true,
            (DelegationState.Queued, DelegationState.NeedsSupervisor) => true,
            (DelegationState.Queued, DelegationState.WaitingForSupervisor) => true,
            (DelegationState.Running, DelegationState.Completed) => true,
            (DelegationState.Running, DelegationState.Failed) => true,
            (DelegationState.Running, DelegationState.Cancelled) => true,
            (DelegationState.Running, DelegationState.BudgetExceeded) => true,
            (DelegationState.Running, DelegationState.NeedsSupervisor) => true,
            (DelegationState.Running, DelegationState.WaitingForSupervisor) => true,
            (DelegationState.WaitingForSupervisor, DelegationState.Running) => true,
            (DelegationState.WaitingForSupervisor, DelegationState.Failed) => true,
            (DelegationState.WaitingForSupervisor, DelegationState.Cancelled) => true,
            (DelegationState.WaitingForSupervisor, DelegationState.BudgetExceeded) => true,
            (DelegationState.WaitingForSupervisor, DelegationState.NeedsSupervisor) => true,
            _ => false,
        };
}
