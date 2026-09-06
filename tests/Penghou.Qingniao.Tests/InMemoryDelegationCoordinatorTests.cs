using FluentAssertions;

namespace Penghou.Qingniao.Tests;

public sealed class InMemoryDelegationCoordinatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Ambiguous_start_recovers_early_handle_without_duplicate_start()
    {
        var provider = new FakeProvider(FakeStartBehavior.ThrowAfterCapture);
        var (coordinator, captures) = CreateCoordinator(provider);
        var request = CreateRequest("ambiguous-start");

        var acceptance = await coordinator.AcceptAsync(Caller(), request, TestContext.Current.CancellationToken);
        var queued = await coordinator.GetAsync(acceptance.DelegationId, TestContext.Current.CancellationToken);
        var running = await coordinator.PumpAsync(acceptance.DelegationId, queued.Progress.Revision, TestContext.Current.CancellationToken);

        running.Progress.State.Should().Be(DelegationState.Running);
        running.Progress.Revision.Should().Be(1);
        running.Progress.WorkerCalls.Should().Be(1);
        provider.StartCalls.Should().Be(1);
        captures.GetSnapshot().Captures.Should().ContainSingle();

        var observed = await coordinator.PumpAsync(acceptance.DelegationId, running.Progress.Revision, TestContext.Current.CancellationToken);
        observed.Progress.State.Should().Be(DelegationState.Running);
        observed.Progress.WorkerCalls.Should().Be(2);
        provider.StartCalls.Should().Be(1);
        provider.ObserveCalls.Should().Be(1);

        var completedObservation = await coordinator.PumpAsync(acceptance.DelegationId, observed.Progress.Revision, TestContext.Current.CancellationToken);
        completedObservation.Progress.State.Should().Be(DelegationState.Running);
        completedObservation.Progress.WorkerCalls.Should().Be(3);
        provider.ObserveCalls.Should().Be(2);

        var completed = await coordinator.PumpAsync(acceptance.DelegationId, completedObservation.Progress.Revision, TestContext.Current.CancellationToken);
        completed.Progress.State.Should().Be(DelegationState.Completed);
        completed.Progress.WorkerCalls.Should().Be(4);
        completed.Result.Should().NotBeNull();
        provider.StartCalls.Should().Be(1);
        provider.GetResultCalls.Should().Be(1);

        var replay = await coordinator.PumpAsync(acceptance.DelegationId, completed.Progress.Revision, TestContext.Current.CancellationToken);
        replay.Should().BeSameAs(completed);
        provider.StartCalls.Should().Be(1);
        provider.ObserveCalls.Should().Be(2);
        provider.GetResultCalls.Should().Be(1);

        var acceptedReplay = await coordinator.AcceptAsync(Caller(), request, TestContext.Current.CancellationToken);
        acceptedReplay.DelegationId.Should().Be(acceptance.DelegationId);
        acceptedReplay.IsNew.Should().BeFalse();
    }

    [Fact]
    public async Task Start_retry_reuses_the_same_external_identity_when_no_handle_was_captured()
    {
        var provider = new FakeProvider(FakeStartBehavior.ThrowBeforeCaptureOnce);
        var (coordinator, _) = CreateCoordinator(provider);
        var acceptance = await coordinator.AcceptAsync(Caller(), CreateRequest("retry-start"), TestContext.Current.CancellationToken);
        var queued = await coordinator.GetAsync(acceptance.DelegationId, TestContext.Current.CancellationToken);

        var first = await coordinator.PumpAsync(acceptance.DelegationId, queued.Progress.Revision, TestContext.Current.CancellationToken);
        var second = await coordinator.PumpAsync(acceptance.DelegationId, first.Progress.Revision, TestContext.Current.CancellationToken);

        provider.StartCalls.Should().Be(2);
        provider.SeenStartRequests.Should().HaveCount(2);
        provider.SeenStartRequests[0].Identity.Should().Be(provider.SeenStartRequests[1].Identity);
        provider.SeenStartRequests[0].Correlation.Should().Be(provider.SeenStartRequests[1].Correlation);
        second.Progress.WorkerCalls.Should().Be(2);
        second.Progress.Revision.Should().Be(2);
    }

    [Fact]
    public async Task Start_retry_budget_is_checked_before_another_provider_call()
    {
        var provider = new FakeProvider(FakeStartBehavior.ThrowBeforeCaptureOnce);
        var (coordinator, _) = CreateCoordinator(provider);
        var acceptance = await coordinator.AcceptAsync(
            Caller(),
            CreateRequest("no-start-retry", maximumRetries: 0),
            TestContext.Current.CancellationToken);
        var queued = await coordinator.GetAsync(
            acceptance.DelegationId,
            TestContext.Current.CancellationToken);

        var first = await coordinator.PumpAsync(
            acceptance.DelegationId,
            queued.Progress.Revision,
            TestContext.Current.CancellationToken);
        var exhausted = await coordinator.PumpAsync(
            acceptance.DelegationId,
            first.Progress.Revision,
            TestContext.Current.CancellationToken);

        exhausted.Progress.State.Should().Be(DelegationState.BudgetExceeded);
        exhausted.Progress.WorkerCalls.Should().Be(1);
        exhausted.Progress.Retries.Should().Be(0);
        provider.StartCalls.Should().Be(1);
    }

    [Theory]
    [InlineData(FakeStartBehavior.ThrowNonRetryableBeforeCapture)]
    [InlineData(FakeStartBehavior.ThrowUnclassifiedBeforeCapture)]
    public async Task Non_retryable_or_untyped_start_failures_are_terminal_without_leaking_exception_text(FakeStartBehavior behavior)
    {
        var provider = new FakeProvider(behavior);
        var (coordinator, _) = CreateCoordinator(provider);
        var acceptance = await coordinator.AcceptAsync(Caller(), CreateRequest("terminal-start"), TestContext.Current.CancellationToken);
        var queued = await coordinator.GetAsync(acceptance.DelegationId, TestContext.Current.CancellationToken);

        var failed = await coordinator.PumpAsync(acceptance.DelegationId, queued.Progress.Revision, TestContext.Current.CancellationToken);

        failed.Progress.State.Should().Be(DelegationState.Failed);
        failed.Result!.Summary.Should().NotContain("sensitive");
        failed.Progress.WorkerCalls.Should().Be(1);
        provider.StartCalls.Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_pumps_are_revision_fenced_and_terminal_replay_is_immutable()
    {
        var provider = new FakeProvider(FakeStartBehavior.ReturnReceipt);
        var (coordinator, _) = CreateCoordinator(provider);
        var acceptance = await coordinator.AcceptAsync(Caller(), CreateRequest("fenced"), TestContext.Current.CancellationToken);
        var queued = await coordinator.GetAsync(acceptance.DelegationId, TestContext.Current.CancellationToken);

        var first = await coordinator.PumpAsync(acceptance.DelegationId, queued.Progress.Revision, TestContext.Current.CancellationToken);
        var stale = () => coordinator.PumpAsync(acceptance.DelegationId, queued.Progress.Revision, TestContext.Current.CancellationToken).AsTask();

        await stale.Should().ThrowAsync<DelegationExecutionStaleException>();
        first.Progress.Revision.Should().Be(1);
    }

    [Fact]
    public async Task Replay_reconstructs_a_preaccepted_queued_execution_when_private_state_is_missing()
    {
        var provider = new FakeProvider(FakeStartBehavior.ReturnReceipt);
        var descriptor = new ProviderDescriptor(
            "fake-provider",
            [new CapabilityDescriptor("agent.execute", 1)]);
        var providers = new InMemoryProviderRegistry();
        providers.Register(descriptor);
        var adapters = new InMemoryExternalOperationProviderCatalog();
        adapters.Register(descriptor, provider);
        var acceptanceRegistry = new InMemoryDelegationAcceptanceRegistry();
        var request = CreateRequest("preaccepted");
        var bound = new InMemoryWorkflowPlanResolver().Resolve(Caller(), request).BoundRequest!;
        var preaccepted = await acceptanceRegistry.AcceptAsync(Caller(), bound, TestContext.Current.CancellationToken);

        var captures = new InMemoryExternalOperationHandleCaptureRegistry();
        var coordinator = new InMemoryDelegationCoordinator(
            acceptanceRegistry,
            new InMemoryWorkflowPlanResolver(),
            providers,
            adapters,
            new SimingExternalOperationSemanticFingerprintVerifier(),
            captures,
            now: () => Start);

        var replay = await coordinator.AcceptAsync(Caller(), request, TestContext.Current.CancellationToken);
        var snapshot = await coordinator.GetAsync(replay.DelegationId, TestContext.Current.CancellationToken);

        replay.DelegationId.Should().Be(preaccepted.DelegationId);
        replay.IsNew.Should().BeFalse();
        snapshot.Progress.State.Should().Be(DelegationState.Queued);
        snapshot.Progress.Revision.Should().Be(0);
    }

    [Fact]
    public async Task Reconstructed_runtime_reuses_deterministic_node_generation_and_start_identity()
    {
        var acceptanceRegistry = new InMemoryDelegationAcceptanceRegistry();
        var providerOne = new FakeProvider(FakeStartBehavior.ReturnReceipt);
        var providerTwo = new FakeProvider(FakeStartBehavior.ReturnReceipt);
        var request = CreateRequest("identity-reconstruction");
        var first = CreateCoordinator(providerOne, acceptanceRegistry: acceptanceRegistry, now: () => Start).Coordinator;
        var accepted = await first.AcceptAsync(Caller(), request, TestContext.Current.CancellationToken);
        var firstQueued = await first.GetAsync(accepted.DelegationId, TestContext.Current.CancellationToken);
        await first.PumpAsync(accepted.DelegationId, firstQueued.Progress.Revision, TestContext.Current.CancellationToken);

        var second = CreateCoordinator(providerTwo, acceptanceRegistry: acceptanceRegistry, now: () => Start).Coordinator;
        var replay = await second.AcceptAsync(Caller(), request, TestContext.Current.CancellationToken);
        var secondQueued = await second.GetAsync(replay.DelegationId, TestContext.Current.CancellationToken);
        await second.PumpAsync(replay.DelegationId, secondQueued.Progress.Revision, TestContext.Current.CancellationToken);

        providerTwo.SeenStartRequests[0].Identity.Should().Be(providerOne.SeenStartRequests[0].Identity);
        providerTwo.SeenStartRequests[0].Correlation.Should().Be(providerOne.SeenStartRequests[0].Correlation);
    }

    [Fact]
    public async Task Duration_budget_is_enforced_at_the_boundary_before_start()
    {
        var clock = Start;
        var provider = new FakeProvider(FakeStartBehavior.ReturnReceipt);
        var (coordinator, _) = CreateCoordinator(provider, now: () => clock);
        var acceptance = await coordinator.AcceptAsync(Caller(), CreateRequest("duration-start", maximumDuration: TimeSpan.FromSeconds(10)), TestContext.Current.CancellationToken);
        var queued = await coordinator.GetAsync(acceptance.DelegationId, TestContext.Current.CancellationToken);
        clock = Start.AddSeconds(10);

        var exceeded = await coordinator.PumpAsync(acceptance.DelegationId, queued.Progress.Revision, TestContext.Current.CancellationToken);

        exceeded.Progress.State.Should().Be(DelegationState.BudgetExceeded);
        exceeded.Result!.BudgetExceeded.Should().NotBeNull();
        exceeded.Result.BudgetExceeded!.Consumed.Value.Should().BeGreaterThan(exceeded.Result.BudgetExceeded.Limit.Value);
        exceeded.Progress.WorkerCalls.Should().Be(0);
        provider.StartCalls.Should().Be(0);
    }

    [Fact]
    public async Task Duration_budget_is_enforced_before_observation_without_another_provider_call()
    {
        var clock = Start;
        var provider = new FakeProvider(FakeStartBehavior.ReturnReceipt);
        var (coordinator, _) = CreateCoordinator(provider, now: () => clock);
        var acceptance = await coordinator.AcceptAsync(Caller(), CreateRequest("duration-observe", maximumDuration: TimeSpan.FromSeconds(10)), TestContext.Current.CancellationToken);
        var queued = await coordinator.GetAsync(acceptance.DelegationId, TestContext.Current.CancellationToken);
        var started = await coordinator.PumpAsync(acceptance.DelegationId, queued.Progress.Revision, TestContext.Current.CancellationToken);
        clock = Start.AddSeconds(10);

        var exceeded = await coordinator.PumpAsync(acceptance.DelegationId, started.Progress.Revision, TestContext.Current.CancellationToken);

        exceeded.Progress.State.Should().Be(DelegationState.BudgetExceeded);
        exceeded.Progress.WorkerCalls.Should().Be(1);
        provider.ObserveCalls.Should().Be(0);
    }

    [Fact]
    public async Task Observe_transport_failure_is_retryable_and_preserves_the_captured_handle()
    {
        var provider = new FakeProvider(FakeStartBehavior.ThrowObserveOnce);
        var (coordinator, _) = CreateCoordinator(provider);
        var acceptance = await coordinator.AcceptAsync(Caller(), CreateRequest("observe-transport"), TestContext.Current.CancellationToken);
        var queued = await coordinator.GetAsync(acceptance.DelegationId, TestContext.Current.CancellationToken);
        var running = await coordinator.PumpAsync(acceptance.DelegationId, queued.Progress.Revision, TestContext.Current.CancellationToken);

        var retry = await coordinator.PumpAsync(acceptance.DelegationId, running.Progress.Revision, TestContext.Current.CancellationToken);
        retry.Progress.State.Should().Be(DelegationState.Running);
        retry.Progress.Retries.Should().Be(1);
        provider.StartCalls.Should().Be(1);

        var observed = await coordinator.PumpAsync(acceptance.DelegationId, retry.Progress.Revision, TestContext.Current.CancellationToken);
        observed.Progress.State.Should().Be(DelegationState.Running);
        provider.StartCalls.Should().Be(1);
    }

    [Fact]
    public async Task Result_transport_failure_is_retryable_and_does_not_become_validation_failure()
    {
        var provider = new FakeProvider(FakeStartBehavior.ThrowResultOnce);
        var (coordinator, _) = CreateCoordinator(provider);
        var acceptance = await coordinator.AcceptAsync(Caller(), CreateRequest("result-transport", maximumWorkerCalls: 5), TestContext.Current.CancellationToken);
        var queued = await coordinator.GetAsync(acceptance.DelegationId, TestContext.Current.CancellationToken);
        var started = await coordinator.PumpAsync(acceptance.DelegationId, queued.Progress.Revision, TestContext.Current.CancellationToken);
        var observing = await coordinator.PumpAsync(acceptance.DelegationId, started.Progress.Revision, TestContext.Current.CancellationToken);
        var ready = await coordinator.PumpAsync(acceptance.DelegationId, observing.Progress.Revision, TestContext.Current.CancellationToken);

        var retry = await coordinator.PumpAsync(acceptance.DelegationId, ready.Progress.Revision, TestContext.Current.CancellationToken);
        retry.Progress.State.Should().Be(DelegationState.Running);
        retry.Result.Should().BeNull();
        retry.Progress.Retries.Should().Be(1);

        var completed = await coordinator.PumpAsync(acceptance.DelegationId, retry.Progress.Revision, TestContext.Current.CancellationToken);
        completed.Progress.State.Should().Be(DelegationState.Completed);
        completed.Result!.UnresolvedConcerns.Should().BeEmpty();
    }

    [Fact]
    public async Task Mismatched_start_receipt_is_terminal_validation_failure_without_synthetic_test_failure()
    {
        var provider = new FakeProvider(FakeStartBehavior.ReturnMismatchedReceipt);
        var (coordinator, _) = CreateCoordinator(provider);
        var acceptance = await coordinator.AcceptAsync(Caller(), CreateRequest("receipt-mismatch"), TestContext.Current.CancellationToken);
        var queued = await coordinator.GetAsync(acceptance.DelegationId, TestContext.Current.CancellationToken);

        var failed = await coordinator.PumpAsync(acceptance.DelegationId, queued.Progress.Revision, TestContext.Current.CancellationToken);

        failed.Progress.State.Should().Be(DelegationState.Failed);
        failed.Result!.Evidence.TestsPassed.Should().Be(0);
        failed.Result.Evidence.TestsFailed.Should().Be(0);
        failed.Result.UnresolvedConcerns.Should().ContainSingle();
        provider.StartCalls.Should().Be(1);
    }

    [Fact]
    public async Task Worker_call_budget_is_checked_before_observation_and_does_not_invoke_provider_again()
    {
        var provider = new FakeProvider(FakeStartBehavior.ReturnReceipt);
        var (coordinator, _) = CreateCoordinator(provider);
        var acceptance = await coordinator.AcceptAsync(Caller(), CreateRequest("worker-budget", maximumWorkerCalls: 1), TestContext.Current.CancellationToken);
        var queued = await coordinator.GetAsync(acceptance.DelegationId, TestContext.Current.CancellationToken);
        var started = await coordinator.PumpAsync(acceptance.DelegationId, queued.Progress.Revision, TestContext.Current.CancellationToken);

        var exhausted = await coordinator.PumpAsync(acceptance.DelegationId, started.Progress.Revision, TestContext.Current.CancellationToken);

        exhausted.Progress.State.Should().Be(DelegationState.BudgetExceeded);
        exhausted.Progress.WorkerCalls.Should().Be(1);
        provider.StartCalls.Should().Be(1);
        provider.ObserveCalls.Should().Be(0);
    }

    [Fact]
    public async Task Result_state_must_agree_with_a_succeeded_observation()
    {
        var provider = new FakeProvider(FakeStartBehavior.ReturnMismatchedResult);
        var (coordinator, _) = CreateCoordinator(provider);
        var acceptance = await coordinator.AcceptAsync(Caller(), CreateRequest("result-state"), TestContext.Current.CancellationToken);
        var queued = await coordinator.GetAsync(acceptance.DelegationId, TestContext.Current.CancellationToken);
        var started = await coordinator.PumpAsync(acceptance.DelegationId, queued.Progress.Revision, TestContext.Current.CancellationToken);
        var observing = await coordinator.PumpAsync(acceptance.DelegationId, started.Progress.Revision, TestContext.Current.CancellationToken);
        var ready = await coordinator.PumpAsync(acceptance.DelegationId, observing.Progress.Revision, TestContext.Current.CancellationToken);

        var failed = await coordinator.PumpAsync(acceptance.DelegationId, ready.Progress.Revision, TestContext.Current.CancellationToken);

        failed.Progress.State.Should().Be(DelegationState.Failed);
        failed.Progress.WorkerCalls.Should().Be(4);
        failed.Result!.UnresolvedConcerns.Should().ContainSingle(concern => concern.Contains("validation", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Result_completion_cannot_precede_the_last_observation()
    {
        var provider = new FakeProvider(FakeStartBehavior.ReturnEarlyResult);
        var (coordinator, _) = CreateCoordinator(provider);
        var acceptance = await coordinator.AcceptAsync(Caller(), CreateRequest("result-time"), TestContext.Current.CancellationToken);
        var queued = await coordinator.GetAsync(acceptance.DelegationId, TestContext.Current.CancellationToken);
        var started = await coordinator.PumpAsync(acceptance.DelegationId, queued.Progress.Revision, TestContext.Current.CancellationToken);
        var observing = await coordinator.PumpAsync(acceptance.DelegationId, started.Progress.Revision, TestContext.Current.CancellationToken);
        var ready = await coordinator.PumpAsync(acceptance.DelegationId, observing.Progress.Revision, TestContext.Current.CancellationToken);

        var failed = await coordinator.PumpAsync(acceptance.DelegationId, ready.Progress.Revision, TestContext.Current.CancellationToken);

        failed.Progress.State.Should().Be(DelegationState.Failed);
        failed.Result!.UnresolvedConcerns.Should().ContainSingle(concern => concern.Contains("validation", StringComparison.OrdinalIgnoreCase));
    }

    private static (InMemoryDelegationCoordinator Coordinator, InMemoryExternalOperationHandleCaptureRegistry Captures) CreateCoordinator(
        FakeProvider provider,
        IDelegationAcceptanceRegistry? acceptanceRegistry = null,
        Func<DateTimeOffset>? now = null)
    {
        var descriptor = new ProviderDescriptor(
            "fake-provider",
            [new CapabilityDescriptor("agent.execute", 1)]);
        var providers = new InMemoryProviderRegistry();
        providers.Register(descriptor);
        var adapters = new InMemoryExternalOperationProviderCatalog();
        adapters.Register(descriptor, provider);
        var captures = new InMemoryExternalOperationHandleCaptureRegistry();
        var coordinator = new InMemoryDelegationCoordinator(
            acceptanceRegistry ?? new InMemoryDelegationAcceptanceRegistry(),
            new InMemoryWorkflowPlanResolver(),
            providers,
            adapters,
            new SimingExternalOperationSemanticFingerprintVerifier(),
            captures,
            now: now ?? (() => Start));
        return (coordinator, captures);
    }

    private static DelegationCallerScope Caller() => new("caller-1");

    private static DelegationRequest CreateRequest(
        string requestKey,
        int maximumWorkerCalls = 4,
        int maximumRetries = 1,
        TimeSpan? maximumDuration = null) => new(
        requestKey,
        "Implement the objective",
        new WorkspaceReference("local", "workspace", "revision"),
        ["The result is correct"],
        [],
        new DelegationBudget(
            MaximumWorkerCalls: maximumWorkerCalls,
            MaximumRetries: maximumRetries,
            MaximumDuration: maximumDuration));

    public enum FakeStartBehavior
    {
        ReturnReceipt,
        ThrowAfterCapture,
        ThrowBeforeCaptureOnce,
        ThrowNonRetryableBeforeCapture,
        ThrowUnclassifiedBeforeCapture,
        ThrowObserveOnce,
        ThrowResultOnce,
        ReturnMismatchedReceipt,
        ReturnMismatchedResult,
        ReturnEarlyResult,
    }

    private sealed class FakeProvider(FakeStartBehavior behavior) : IExternalOperationProvider
    {
        private static readonly DateTimeOffset AcceptedAt = Start.AddMinutes(1);
        private ExternalOperationHandle? handle;

        public int StartCalls { get; private set; }
        public int ObserveCalls { get; private set; }
        public int GetResultCalls { get; private set; }
        public List<ExternalOperationStartRequest> SeenStartRequests { get; } = [];

        public async ValueTask<ExternalOperationStartReceipt> StartAsync(
            ExternalOperationStartRequest request,
            IExternalOperationHandleCaptureSink handleSink,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCalls++;
            SeenStartRequests.Add(request);
            handle ??= CreateHandle(request);

            if (behavior == FakeStartBehavior.ThrowBeforeCaptureOnce && StartCalls == 1)
            {
                throw new ExternalOperationProviderException(new ExternalOperationFailure(
                    ExternalOperationFailureKind.Transport,
                    "start.transport-lost",
                    "The transport acknowledgement was lost before capture.",
                    retryable: true));
            }

            if (behavior == FakeStartBehavior.ThrowNonRetryableBeforeCapture)
            {
                throw new ExternalOperationProviderException(new ExternalOperationFailure(
                    ExternalOperationFailureKind.Rejection,
                    "start.rejected",
                    "sensitive provider details",
                    retryable: false));
            }

            if (behavior == FakeStartBehavior.ThrowUnclassifiedBeforeCapture)
            {
                throw new InvalidOperationException("sensitive provider details");
            }

            await handleSink.CaptureAsync(
                new ExternalOperationHandleCapture(handle, AcceptedAt),
                cancellationToken);

            if (behavior == FakeStartBehavior.ThrowAfterCapture)
            {
                throw new InvalidOperationException("The provider accepted the task but lost its response.");
            }

            if (behavior == FakeStartBehavior.ReturnMismatchedReceipt)
            {
                var mismatchedIdentity = new ExternalOperationStartIdentity(
                    request.Identity.DelegationId,
                    request.Identity.WorkflowRun,
                    request.Identity.StructuralNode,
                    request.Identity.NodeGeneration,
                    request.Identity.ExecutionAttemptId,
                    "mismatched-start-key",
                    new string('b', 64));
                return new ExternalOperationStartReceipt(
                    mismatchedIdentity,
                    handle,
                    ExternalOperationStartDisposition.Created,
                    ExternalOperationState.Running,
                    AcceptedAt);
            }

            return new ExternalOperationStartReceipt(
                request.Identity,
                handle,
                ExternalOperationStartDisposition.Created,
                ExternalOperationState.Running,
                AcceptedAt);
        }

        public ValueTask<ExternalOperationObservation> ObserveAsync(
            ExternalOperationHandle operationHandle,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            operationHandle.Should().Be(handle);
            if (behavior == FakeStartBehavior.ThrowObserveOnce && ObserveCalls == 0)
            {
                ObserveCalls++;
                throw new ExternalOperationProviderException(new ExternalOperationFailure(
                    ExternalOperationFailureKind.Transport,
                    "observe.transport-unavailable",
                    "The observation transport was unavailable.",
                    retryable: true));
            }

            ObserveCalls++;
            var state = ObserveCalls <= 1 ? ExternalOperationState.Running : ExternalOperationState.Succeeded;
            return ValueTask.FromResult(new ExternalOperationObservation(
                operationHandle,
                ObserveCalls,
                state,
                AcceptedAt.AddMinutes(ObserveCalls),
                resultAvailable: state == ExternalOperationState.Succeeded));
        }

        public ValueTask<ExternalOperationResult> GetResultAsync(
            ExternalOperationHandle operationHandle,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            operationHandle.Should().Be(handle);
            GetResultCalls++;
            if (behavior == FakeStartBehavior.ThrowResultOnce && GetResultCalls == 1)
            {
                throw new ExternalOperationProviderException(new ExternalOperationFailure(
                    ExternalOperationFailureKind.Transport,
                    "result.transport-unavailable",
                    "The result transport was unavailable.",
                    retryable: true));
            }

            var artifact = new DelegationArtifactReference(
                operationHandle.Correlation.DelegationId,
                operationHandle.Correlation.StructuralNode,
                operationHandle.Correlation.NodeGeneration,
                "fake-provider",
                "fake-repository",
                "artifact-1",
                "text/plain",
                1,
                "memory://artifact-1",
                ArtifactContentIdentity.Sha256Bytes("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"));
            var resultState = behavior == FakeStartBehavior.ReturnMismatchedResult
                ? ExternalOperationState.Failed
                : ExternalOperationState.Succeeded;
            var completedAt = behavior == FakeStartBehavior.ReturnEarlyResult
                ? AcceptedAt
                : AcceptedAt.AddMinutes(3);
            var failure = resultState == ExternalOperationState.Failed
                ? new ExternalOperationFailure(
                    ExternalOperationFailureKind.Remote,
                    "remote.failed",
                    "The fake provider reported a remote failure.",
                    retryable: false)
                : null;
            return ValueTask.FromResult(new ExternalOperationResult(
                operationHandle,
                resultState,
                completedAt,
                "completed",
                [artifact],
                failure: failure));
        }

        public ValueTask<ExternalOperationCancellationReceipt> CancelAsync(
            ExternalOperationCancelRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<ExternalOperationResumeReceipt> ResumeAsync(
            ExternalOperationResumeRequest request,
            IExternalOperationHandleCaptureSink handleSink,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        private static ExternalOperationHandle CreateHandle(ExternalOperationStartRequest request)
        {
            var correlation = new ExternalOperationCorrelation(
                request.Correlation.DelegationId,
                request.Correlation.WorkflowRun,
                request.Correlation.StructuralNode,
                request.Correlation.NodeGeneration,
                request.Correlation.ExecutionAttemptId,
                request.Correlation.Agent,
                new ExternalTaskReference(request.Correlation.Agent.Provider, "task-1"));
            return new ExternalOperationHandle(
                request.Correlation.Agent.Provider,
                "handle-1",
                request.Correlation.Agent.ProtocolVersion,
                correlation);
        }
    }
}
