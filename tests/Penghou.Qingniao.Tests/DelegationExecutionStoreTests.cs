using FluentAssertions;

namespace Penghou.Qingniao.Tests;

public sealed class DelegationExecutionStoreTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Creation_publishes_queued_revision_zero_and_is_idempotent()
    {
        var store = new InMemoryDelegationExecutionStore();
        var id = Id(1);
        var first = await store.CreateAsync(id, Start, TestContext.Current.CancellationToken);
        var replay = await store.CreateAsync(id, Start, TestContext.Current.CancellationToken);

        first.Progress.State.Should().Be(DelegationState.Queued);
        first.Progress.Revision.Should().Be(0);
        first.Result.Should().BeNull();
        replay.Should().BeSameAs(first);
    }

    [Fact]
    public async Task Concurrent_creation_returns_one_idempotent_aggregate()
    {
        var store = new InMemoryDelegationExecutionStore();
        var id = Id(7);
        var calls = Enumerable.Range(0, 64)
            .Select(_ => Task.Factory.StartNew(
                () => store.CreateAsync(id, Start, TestContext.Current.CancellationToken).AsTask(),
                TestContext.Current.CancellationToken,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default)
                .Unwrap())
            .ToArray();

        var snapshots = await Task.WhenAll(calls);

        snapshots.Should().OnlyContain(snapshot => snapshot.Progress.State == DelegationState.Queued);
        snapshots.Should().OnlyContain(snapshot => snapshot.Progress.Revision == 0);
        snapshots.Should().OnlyContain(snapshot => snapshot.Result == null);
        (await store.GetAsync(id, TestContext.Current.CancellationToken)).Should().BeSameAs(snapshots[0]);
        store.List().Should().ContainSingle();
    }

    [Fact]
    public async Task Ownership_stale_and_changed_revision_writes_are_rejected()
    {
        var store = new InMemoryDelegationExecutionStore();
        var id = Id(2);
        await store.CreateAsync(id, Start, TestContext.Current.CancellationToken);
        var running = Progress(id, DelegationState.Running, 1, 1);
        await store.PublishProgressAsync(id, running, TestContext.Current.CancellationToken);

        var wrongOwner = () => store.PublishProgressAsync(Id(99), running, TestContext.Current.CancellationToken).AsTask();
        var stale = () => store.PublishProgressAsync(id, Progress(id, DelegationState.Running, 0, 2), TestContext.Current.CancellationToken).AsTask();
        var changedReplay = () => store.PublishProgressAsync(id, Progress(id, DelegationState.Running, 1, 2), TestContext.Current.CancellationToken).AsTask();

        (await wrongOwner.Should().ThrowAsync<DelegationExecutionConflictException>()).Which.Kind
            .Should().Be(DelegationExecutionConflictKind.Ownership);
        (await stale.Should().ThrowAsync<DelegationExecutionStaleException>()).Which.SuppliedRevision.Should().Be(0);
        (await changedReplay.Should().ThrowAsync<DelegationExecutionConflictException>()).Which.Kind
            .Should().Be(DelegationExecutionConflictKind.InitialSnapshot);
    }

    [Fact]
    public async Task Terminal_publication_is_atomic_and_exact_replays_are_idempotent()
    {
        var store = new InMemoryDelegationExecutionStore();
        var id = Id(3);
        await store.CreateAsync(id, Start, TestContext.Current.CancellationToken);
        await store.PublishProgressAsync(id, Progress(id, DelegationState.Running, 1, 1), TestContext.Current.CancellationToken);

        var terminalProgress = Progress(id, DelegationState.Completed, 2, 2);
        var result = Result(id, 2, cancellationToken: TestContext.Current.CancellationToken);
        var published = await store.PublishTerminalAsync(id, terminalProgress, result, TestContext.Current.CancellationToken);
        var replay = await store.PublishTerminalAsync(id, Progress(id, DelegationState.Completed, 2, 2), Result(id, 2, cancellationToken: TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        published.Result.Should().NotBeNull();
        replay.Should().BeSameAs(published);
        var terminalReplay = () => store.PublishProgressAsync(id, terminalProgress, TestContext.Current.CancellationToken).AsTask();
        await terminalReplay.Should().ThrowAsync<DelegationExecutionConflictException>();
    }

    [Fact]
    public async Task Terminal_replacement_and_missing_pair_are_rejected_without_partial_publication()
    {
        var store = new InMemoryDelegationExecutionStore();
        var id = Id(4);
        await store.CreateAsync(id, Start, TestContext.Current.CancellationToken);
        await store.PublishProgressAsync(id, Progress(id, DelegationState.Running, 1, 1), TestContext.Current.CancellationToken);
        var terminalProgress = Progress(id, DelegationState.Completed, 2, 2);
        var result = Result(id, 2, cancellationToken: TestContext.Current.CancellationToken);

        var missingResult = () => store.PublishProgressAsync(id, terminalProgress, TestContext.Current.CancellationToken).AsTask();
        (await missingResult.Should().ThrowAsync<DelegationExecutionConflictException>()).Which.Kind
            .Should().Be(DelegationExecutionConflictKind.TerminalAtomicity);
        (await store.GetAsync(id, TestContext.Current.CancellationToken)).Progress.State.Should().Be(DelegationState.Running);
        (await store.GetAsync(id, TestContext.Current.CancellationToken)).Result.Should().BeNull();

        await store.PublishTerminalAsync(id, terminalProgress, result, TestContext.Current.CancellationToken);
        var replacement = () => store.PublishTerminalAsync(
            id,
            Progress(id, DelegationState.Completed, 3, 3),
            Result(id, 3, "rewritten"),
            TestContext.Current.CancellationToken).AsTask();
        (await replacement.Should().ThrowAsync<DelegationExecutionConflictException>()).Which.Kind
            .Should().Be(DelegationExecutionConflictKind.TerminalReplacement);
        (await store.GetAsync(id, TestContext.Current.CancellationToken)).Result!.Summary.Should().Be("done");
    }

    [Theory]
    [InlineData("summary")]
    [InlineData("artifacts")]
    [InlineData("normalized-evidence")]
    [InlineData("completion-time")]
    [InlineData("state")]
    public async Task Terminal_replacement_rejects_each_semantic_change(string change)
    {
        var store = new InMemoryDelegationExecutionStore();
        var id = Id(8);
        await store.CreateAsync(id, Start, TestContext.Current.CancellationToken);
        await store.PublishProgressAsync(id, Progress(id, DelegationState.Running, 1, 1), TestContext.Current.CancellationToken);
        await store.PublishTerminalAsync(
            id,
            Progress(id, DelegationState.Completed, 2, 2),
            Result(id, 2, cancellationToken: TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        var state = change == "state" ? DelegationState.Failed : DelegationState.Completed;
        var candidate = Result(
            id,
            3,
            change == "summary" ? "changed" : "done",
            state,
            change == "artifacts" ? [CreateArtifact(id, "artifact-2")] : null,
            change == "normalized-evidence" ? new EvidenceBundle([CreateInvocation(id, "replacement")]) : null,
            change == "completion-time" ? Start.AddMinutes(4) : null,
            TestContext.Current.CancellationToken);
        var candidateProgress = Progress(id, state, 3, 3);

        var act = () => store.PublishTerminalAsync(id, candidateProgress, candidate, TestContext.Current.CancellationToken).AsTask();
        await act.Should().ThrowAsync<DelegationExecutionConflictException>();
        (await store.GetAsync(id, TestContext.Current.CancellationToken)).Result!.Summary.Should().Be("done");
    }

    [Fact]
    public async Task Source_collections_cannot_mutate_stored_snapshots()
    {
        var store = new InMemoryDelegationExecutionStore();
        var id = Id(9);
        var currentSteps = new List<string> { "inspect" };
        var completedSteps = new List<string> { "prepare" };
        var changedFiles = new List<string> { "README.md" };
        var concerns = new List<string> { "none" };
        var queued = new DelegationProgress(id, DelegationState.Queued, 0, currentSteps, completedSteps, 0, 0, Start);
        var queuedSnapshot = await store.CreateAsync(queued, TestContext.Current.CancellationToken);
        currentSteps[0] = "mutated";
        completedSteps.Add("mutated");
        queuedSnapshot.Progress.CurrentSteps.Should().Equal("inspect");
        queuedSnapshot.Progress.CompletedSteps.Should().Equal("prepare");

        await store.PublishProgressAsync(id, Progress(id, DelegationState.Running, 1, 1), TestContext.Current.CancellationToken);
        var result = new DelegationResult(
            id,
            DelegationState.Completed,
            "done",
            new DelegationEvidence(changedFiles, [], 1, 0, true, 0),
            [],
            concerns,
            Start.AddMinutes(2));
        await store.PublishTerminalAsync(id, Progress(id, DelegationState.Completed, 2, 2), result, TestContext.Current.CancellationToken);
        changedFiles[0] = "mutated";
        concerns[0] = "mutated";

        var snapshot = await store.GetAsync(id, TestContext.Current.CancellationToken);
        snapshot.Result!.Evidence.ChangedFiles.Should().Equal("README.md");
        snapshot.Result.UnresolvedConcerns.Should().Equal("none");
    }

    [Fact]
    public async Task Concurrent_progress_publication_keeps_one_valid_revision()
    {
        var store = new InMemoryDelegationExecutionStore();
        var id = Id(6);
        await store.CreateAsync(id, Start, TestContext.Current.CancellationToken);

        var calls = Enumerable.Range(0, 64)
            .Select(_ => Task.Factory.StartNew(
                () => store.PublishProgressAsync(id, Progress(id, DelegationState.Running, 1, 1), TestContext.Current.CancellationToken).AsTask(),
                TestContext.Current.CancellationToken,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default)
                .Unwrap())
            .ToArray();
        var snapshots = await Task.WhenAll(calls);

        snapshots.Should().OnlyContain(snapshot => snapshot.Progress.State == DelegationState.Running);
        snapshots.Should().OnlyContain(snapshot => snapshot.Progress.Revision == 1);
        (await store.GetAsync(id, TestContext.Current.CancellationToken)).Should().BeSameAs(snapshots[0]);
    }

    [Fact]
    public async Task Concurrent_exact_terminal_publications_share_one_immutable_snapshot()
    {
        var store = new InMemoryDelegationExecutionStore();
        var id = Id(5);
        await store.CreateAsync(id, Start, TestContext.Current.CancellationToken);
        await store.PublishProgressAsync(id, Progress(id, DelegationState.Running, 1, 1), TestContext.Current.CancellationToken);

        var operations = Enumerable.Range(0, 64)
            .Select(_ => Task.Factory.StartNew(
                () => store.PublishTerminalAsync(id, Progress(id, DelegationState.Completed, 2, 2), Result(id, 2), TestContext.Current.CancellationToken).AsTask(),
                TestContext.Current.CancellationToken,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default)
                .Unwrap())
            .ToArray();
        var snapshots = await Task.WhenAll(operations);

        snapshots.Should().OnlyContain(snapshot => snapshot.Progress.Revision == 2);
        snapshots.Should().OnlyContain(snapshot => snapshot.Result!.Summary == "done");
        (await store.GetAsync(id, TestContext.Current.CancellationToken)).Should().BeSameAs(snapshots[0]);
    }

    [Fact]
    public async Task Listing_is_bounded_deterministic_and_cancellation_does_not_mutate()
    {
        var store = new InMemoryDelegationExecutionStore(2);
        await store.CreateAsync(Id(2), Start, TestContext.Current.CancellationToken);
        await store.CreateAsync(Id(1), Start, TestContext.Current.CancellationToken);
        store.List().Select(snapshot => snapshot.Progress.DelegationId).Should().Equal(Id(1), Id(2));
        var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var act = () => store.GetAsync(Id(1), cancellation.Token).AsTask();
        await act.Should().ThrowAsync<OperationCanceledException>();
        store.List().Should().HaveCount(2);
        var overCapacity = () => store.CreateAsync(Id(3), Start, TestContext.Current.CancellationToken).AsTask();
        await overCapacity.Should().ThrowAsync<InvalidOperationException>();
    }

    private static DelegationId Id(int value) => new(new Guid(value, 0, 0, new byte[8]));

    private static DelegationProgress Progress(
        DelegationId id,
        DelegationState state,
        long revision,
        int offset) => new(id, state, revision, [], [], 0, 0, Start.AddMinutes(offset));

    private static DelegationResult Result(
        DelegationId id,
        int offset,
        string summary = "done",
        DelegationState state = DelegationState.Completed,
        IReadOnlyList<DelegationArtifactReference>? artifacts = null,
        EvidenceBundle? normalizedEvidence = null,
        DateTimeOffset? completedAt = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new DelegationResult(
        id,
        state,
        summary,
        new DelegationEvidence([], [], 1, 0, true, 0),
        artifacts ?? [],
        [],
        completedAt ?? Start.AddMinutes(offset),
        normalizedEvidence);
    }

    private static DelegationArtifactReference CreateArtifact(DelegationId id, string artifactId) => new(
        id,
        new StructuralNodeReference("implement"),
        new NodeGenerationId(new Guid(11, 0, 0, new byte[8])),
        "provider",
        "repository",
        artifactId,
        "application/json",
        1,
        "artifact-location",
        ArtifactContentIdentity.Sha256Bytes("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"));

    private static WorkerInvocationEvidence CreateInvocation(DelegationId id, string attemptId) => new(
        id,
        new StructuralNodeReference("implement"),
        new NodeGenerationId(new Guid(11, 0, 0, new byte[8])),
        EvidenceKinds.AgentExecution,
        new ProviderExecutionAttemptReference("provider", attemptId, $"handle-{attemptId}"),
        "completed",
        Start,
        Start.AddMinutes(1),
        "capability",
        "profile",
        "requested-provider",
        "requested-model",
        "resolved-model",
        ["read-files"],
        [CreateArtifact(id, "input-artifact")],
        []);
}
