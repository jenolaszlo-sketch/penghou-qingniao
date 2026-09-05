using FluentAssertions;

namespace Penghou.Qingniao.Tests;

public sealed class ExternalOperationHandleCaptureRegistryTests
{
    [Fact]
    public async Task Same_operation_and_handle_replay_is_idempotent_and_retains_the_earliest_capture()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var registry = new InMemoryExternalOperationHandleCaptureRegistry();
        var capture = Capture("attempt-1", "handle-1", "task-1", 1);

        await registry.CaptureAsync(capture, cancellationToken);
        await registry.CaptureAsync(new ExternalOperationHandleCapture(
            new ExternalOperationHandle(
                "a2a",
                "handle-1",
                "a2a-0.3",
                Correlation("attempt-1", "task-1")),
            At(2)), cancellationToken);

        registry.GetSnapshot().Captures.Should().ContainSingle().Which.Should().BeSameAs(capture);
        await registry.CaptureAsync(Capture("attempt-1", "handle-1", "task-1", 0), cancellationToken);
        registry.GetSnapshot().Captures.Should().ContainSingle().Which.CapturedAt.Should().Be(At(0));
        registry.TryGet(capture.Handle.Correlation, out var byCorrelation).Should().BeTrue();
        byCorrelation!.CapturedAt.Should().Be(At(0));
        registry.TryGet(capture.Handle, out var byHandle).Should().BeTrue();
        byHandle!.CapturedAt.Should().Be(At(0));
    }

    [Fact]
    public async Task Conflicting_capture_for_the_same_execution_attempt_is_rejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var registry = new InMemoryExternalOperationHandleCaptureRegistry();
        var existing = Capture("attempt-1", "handle-1", "task-1", 1);
        var conflicting = Capture("attempt-1", "handle-2", "task-1", 2);
        await registry.CaptureAsync(existing, cancellationToken);

        var act = () => registry.CaptureAsync(conflicting, cancellationToken).AsTask();

        var exception = await act.Should().ThrowAsync<ExternalOperationHandleCaptureConflictException>();
        exception.Which.Kind.Should().Be(ExternalOperationHandleCaptureConflictKind.ExecutionIdentity);
        registry.GetSnapshot().Captures.Should().ContainSingle().Which.Should().BeSameAs(existing);
    }

    [Fact]
    public async Task A_provider_handle_cannot_be_reused_by_another_execution()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var registry = new InMemoryExternalOperationHandleCaptureRegistry();
        var existing = Capture("attempt-1", "handle-1", "task-1", 1);
        var conflicting = Capture("attempt-2", "handle-1", "task-2", 2);
        await registry.CaptureAsync(existing, cancellationToken);

        var act = () => registry.CaptureAsync(conflicting, cancellationToken).AsTask();

        var exception = await act.Should().ThrowAsync<ExternalOperationHandleCaptureConflictException>();
        exception.Which.Kind.Should().Be(ExternalOperationHandleCaptureConflictKind.ReusedHandle);
        registry.GetSnapshot().Captures.Should().ContainSingle().Which.Should().BeSameAs(existing);
    }

    [Fact]
    public async Task Capacity_is_bounded_and_exact_replays_do_not_consume_capacity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var registry = new InMemoryExternalOperationHandleCaptureRegistry(2);
        await registry.CaptureAsync(Capture("attempt-1", "handle-1", "task-1", 1), cancellationToken);
        await registry.CaptureAsync(Capture("attempt-2", "handle-2", "task-2", 2), cancellationToken);

        await registry.CaptureAsync(Capture("attempt-1", "handle-1", "task-1", 1), cancellationToken);
        var act = () => registry.CaptureAsync(Capture("attempt-3", "handle-3", "task-3", 3), cancellationToken).AsTask();

        await act.Should().ThrowAsync<InvalidOperationException>();
        registry.GetSnapshot().Count.Should().Be(2);
    }

    [Fact]
    public async Task Cancellation_is_observed_before_validation_and_mutation()
    {
        var registry = new InMemoryExternalOperationHandleCaptureRegistry();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var act = () => registry.CaptureAsync(null!, cancellation.Token).AsTask();

        await act.Should().ThrowAsync<OperationCanceledException>();
        registry.GetSnapshot().Captures.Should().BeEmpty();
    }

    [Fact]
    public async Task Snapshots_are_immutable_and_isolated_from_later_captures()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var registry = new InMemoryExternalOperationHandleCaptureRegistry();
        var first = Capture("attempt-1", "handle-1", "task-1", 1);
        await registry.CaptureAsync(first, cancellationToken);
        var snapshot = registry.GetSnapshot();
        await registry.CaptureAsync(Capture("attempt-2", "handle-2", "task-2", 2), cancellationToken);

        snapshot.Captures.Should().ContainSingle().Which.Should().BeSameAs(first);
        registry.GetSnapshot().Captures.Should().HaveCount(2);
        var mutate = () => ((IList<ExternalOperationHandleCapture>)snapshot.Captures).Add(first);
        mutate.Should().Throw<NotSupportedException>();
        snapshot.TryGet(first.Handle.Correlation, out var found).Should().BeTrue();
        found.Should().BeSameAs(first);
    }

    [Fact]
    public async Task Snapshots_are_sorted_by_stable_execution_key()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var registry = new InMemoryExternalOperationHandleCaptureRegistry();

        await registry.CaptureAsync(Capture("attempt-z", "handle-z", "task-z", 1), cancellationToken);
        await registry.CaptureAsync(Capture("attempt-a", "handle-a", "task-a", 2), cancellationToken);

        registry.GetSnapshot().Captures
            .Select(capture => capture.Handle.Correlation.ExecutionAttemptId)
            .Should()
            .Equal("attempt-a", "attempt-z");
    }

    [Fact]
    public async Task Concurrent_captures_are_atomic_and_preserve_one_entry_per_execution()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var registry = new InMemoryExternalOperationHandleCaptureRegistry(64);
        var captures = Enumerable.Range(0, 64)
            .Select(index => Capture($"attempt-{index}", $"handle-{index}", $"task-{index}", index + 1))
            .ToArray();

        await Task.WhenAll(captures.Select(capture => registry.CaptureAsync(capture, cancellationToken).AsTask()));

        registry.GetSnapshot().Captures.Should().HaveCount(captures.Length);
        foreach (var capture in captures)
        {
            registry.TryGet(capture.Handle.Correlation, out var found).Should().BeTrue();
            found.Should().BeSameAs(capture);
        }
    }

    [Fact]
    public async Task Constructor_and_read_lookups_validate_inputs()
    {
        var belowMinimum = () => new InMemoryExternalOperationHandleCaptureRegistry(0);
        var aboveMaximum = () => new InMemoryExternalOperationHandleCaptureRegistry(InMemoryExternalOperationHandleCaptureRegistry.MaximumEntries + 1);
        var registry = new InMemoryExternalOperationHandleCaptureRegistry();

        belowMinimum.Should().Throw<ArgumentOutOfRangeException>();
        aboveMaximum.Should().Throw<ArgumentOutOfRangeException>();
        var nullLookup = () => registry.TryGet((ExternalOperationCorrelation)null!, out _);
        nullLookup.Should().Throw<ArgumentNullException>();

        var capture = Capture("attempt-1", "handle-1", "task-1", 1);
        await registry.CaptureAsync(capture, TestContext.Current.CancellationToken);
        registry.TryGet(Correlation("attempt-1", null), out var recovered).Should().BeTrue();
        recovered.Should().BeSameAs(capture);
    }

    private static ExternalOperationHandleCapture Capture(string attempt, string handle, string task, int seconds) =>
        new(new ExternalOperationHandle("a2a", handle, "a2a-0.3", Correlation(attempt, task)), At(seconds));

    private static ExternalOperationCorrelation Correlation(string attempt, string? task) => new(
        DelegationIdValue,
        Workflow,
        new StructuralNodeReference("implement"),
        new NodeGenerationId(Generation),
        attempt,
        new ExternalAgentReference("a2a", "agent-1", "a2a-0.3"),
        task is null ? null : new ExternalTaskReference("a2a", task));

    private static DateTimeOffset At(int seconds) => DateTimeOffset.Parse("2026-01-01T00:00:00Z").AddSeconds(seconds);

    private static readonly DelegationId DelegationIdValue = new(Guid.Parse("00000000-0000-0000-0000-000000000101"));
    private static readonly WorkflowRunExecutionReference Workflow = new("zhinu", "run-1", "epoch-1");
    private static readonly Guid Generation = Guid.Parse("00000000-0000-0000-0000-000000000111");
}
