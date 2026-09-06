#pragma warning disable xUnit1051

using FluentAssertions;

namespace Penghou.Qingniao.Tests;

public sealed class M24CoordinatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Queued_cancel_is_terminal_without_a_provider_call_and_replays_immutably()
    {
        var provider = new M24Provider();
        var (coordinator, _) = CreateCoordinator(provider);
        var accepted = await coordinator.AcceptAsync(new DelegationCallerScope("caller"), Request("queued"));
        var queued = await coordinator.GetAsync(accepted.DelegationId);

        var cancelled = await coordinator.CancelAsync(
            accepted.DelegationId,
            queued.Progress.Revision,
            "cancel-1",
            "Stop before execution.");
        var replay = await coordinator.CancelAsync(
            accepted.DelegationId,
            0,
            "cancel-1",
            "Stop before execution.");

        cancelled.Progress.State.Should().Be(DelegationState.Cancelled);
        cancelled.Progress.WorkerCalls.Should().Be(0);
        replay.Progress.Should().BeSameAs(cancelled.Progress);
        provider.TotalCalls.Should().Be(0);
    }

    [Fact]
    public async Task Running_cancel_uses_the_exact_key_and_can_replay_after_the_revision_moves()
    {
        var provider = new M24Provider { CancellationDisposition = ExternalOperationCancellationDisposition.ConfirmedCancelled };
        var (coordinator, _) = CreateCoordinator(provider);
        var accepted = await coordinator.AcceptAsync(new DelegationCallerScope("caller"), Request("running"));
        var queued = await coordinator.GetAsync(accepted.DelegationId);
        var running = await coordinator.PumpAsync(accepted.DelegationId, queued.Progress.Revision);

        var cancelled = await coordinator.CancelAsync(accepted.DelegationId, running.Progress.Revision, "cancel-1", "Stop now.");
        var replay = await coordinator.CancelAsync(accepted.DelegationId, running.Progress.Revision, "cancel-1", "Stop now.");

        cancelled.Progress.State.Should().Be(DelegationState.Cancelled);
        cancelled.Progress.WorkerCalls.Should().Be(2);
        replay.Progress.Should().BeSameAs(cancelled.Progress);
        provider.CancelCalls.Should().Be(1);
        var conflict = () => coordinator.CancelAsync(accepted.DelegationId, 0, "cancel-1", "Changed reason.").AsTask();
        await conflict.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Caller_transport_cancellation_does_not_create_durable_cancel_intent()
    {
        var provider = new M24Provider { CancellationDisposition = ExternalOperationCancellationDisposition.ConfirmedCancelled };
        var (coordinator, _) = CreateCoordinator(provider);
        var accepted = await coordinator.AcceptAsync(new DelegationCallerScope("caller"), Request("transport"));
        var queued = await coordinator.GetAsync(accepted.DelegationId);
        var running = await coordinator.PumpAsync(accepted.DelegationId, queued.Progress.Revision);
        using var transport = new CancellationTokenSource();
        transport.Cancel();

        var cancelledCall = () => coordinator.CancelAsync(
            accepted.DelegationId,
            running.Progress.Revision,
            "cancel-transport",
            "Stop now.",
            transport.Token).AsTask();
        await cancelledCall.Should().ThrowAsync<OperationCanceledException>();
        provider.CancelCalls.Should().Be(0);

        var cancelled = await coordinator.CancelAsync(accepted.DelegationId, running.Progress.Revision, "cancel-transport", "Stop now.");
        cancelled.Progress.State.Should().Be(DelegationState.Cancelled);
        provider.CancelCalls.Should().Be(1);
    }

    [Fact]
    public async Task Waiting_resume_accepts_a_safe_handle_rotation_and_does_not_duplicate_exact_replay()
    {
        var provider = new M24Provider { WaitingBeforeResume = true };
        var (coordinator, captures) = CreateCoordinator(provider);
        var accepted = await coordinator.AcceptAsync(new DelegationCallerScope("caller"), Request("resume"));
        var queued = await coordinator.GetAsync(accepted.DelegationId);
        var running = await coordinator.PumpAsync(accepted.DelegationId, queued.Progress.Revision);
        var waiting = await coordinator.PumpAsync(accepted.DelegationId, running.Progress.Revision);
        var supervisor = new SupervisorIdentity("m24", "operator");
        await coordinator.ActivateCheckpointAsync(accepted.DelegationId, supervisor);
        var intervention = new SupervisorIntervention(accepted.DelegationId, waiting.Progress.Checkpoint!.CheckpointId, "resume-1", waiting.Progress.Revision, new SupervisorAction.Approve("Continue."));

        var resumed = await coordinator.ApplyInterventionAsync(supervisor, intervention);
        var replay = await coordinator.ApplyInterventionAsync(supervisor, intervention);

        resumed.Progress.State.Should().Be(DelegationState.Running);
        replay.Progress.Should().BeSameAs(resumed.Progress);
        provider.ResumeCalls.Should().Be(1);
        captures.GetSnapshot().Captures.Should().ContainSingle().Which.Handle.Value.Should().Be("handle-rotated");
    }

    [Fact]
    public async Task Rotated_handle_capture_with_lost_resume_receipt_is_observed_without_a_duplicate_resume()
    {
        var provider = new M24Provider { WaitingBeforeResume = true, LoseResumeReceiptAfterCapture = true };
        var (coordinator, _) = CreateCoordinator(provider);
        var accepted = await coordinator.AcceptAsync(new DelegationCallerScope("caller"), Request("resume-ambiguous"));
        var queued = await coordinator.GetAsync(accepted.DelegationId);
        var running = await coordinator.PumpAsync(accepted.DelegationId, queued.Progress.Revision);
        var waiting = await coordinator.PumpAsync(accepted.DelegationId, running.Progress.Revision);
        var supervisor = new SupervisorIdentity("m24", "operator");
        await coordinator.ActivateCheckpointAsync(accepted.DelegationId, supervisor);
        var intervention = new SupervisorIntervention(accepted.DelegationId, waiting.Progress.Checkpoint!.CheckpointId, "resume-ambiguous-1", waiting.Progress.Revision, new SupervisorAction.Approve("Continue."));

        var first = await coordinator.ApplyInterventionAsync(supervisor, intervention);
        var replay = await coordinator.ApplyInterventionAsync(supervisor, intervention);

        first.Progress.State.Should().Be(DelegationState.Running);
        replay.Progress.State.Should().Be(DelegationState.Running);
        provider.ResumeCalls.Should().Be(1);
        provider.ObserveCalls.Should().Be(2);
    }

    [Fact]
    public async Task Resume_retries_consume_worker_and_retry_budgets()
    {
        var provider = new M24Provider { WaitingBeforeResume = true, FailResumeWithoutCapture = true };
        var (coordinator, _) = CreateCoordinator(provider);
        var accepted = await coordinator.AcceptAsync(new DelegationCallerScope("caller"), Request("resume-budget"));
        var queued = await coordinator.GetAsync(accepted.DelegationId);
        var running = await coordinator.PumpAsync(accepted.DelegationId, queued.Progress.Revision);
        var waiting = await coordinator.PumpAsync(accepted.DelegationId, running.Progress.Revision);
        var supervisor = new SupervisorIdentity("m24", "operator");
        await coordinator.ActivateCheckpointAsync(accepted.DelegationId, supervisor);
        var intervention = new SupervisorIntervention(accepted.DelegationId, waiting.Progress.Checkpoint!.CheckpointId, "resume-budget-1", waiting.Progress.Revision, new SupervisorAction.Approve());

        var first = await coordinator.ApplyInterventionAsync(supervisor, intervention);
        var second = await coordinator.ApplyInterventionAsync(supervisor, intervention);
        var exhausted = await coordinator.ApplyInterventionAsync(supervisor, intervention);

        first.Progress.State.Should().Be(DelegationState.Running);
        second.Progress.State.Should().Be(DelegationState.Running);
        exhausted.Progress.State.Should().Be(DelegationState.Failed);
        exhausted.Progress.WorkerCalls.Should().Be(4);
        exhausted.Progress.Retries.Should().Be(1);
        provider.ResumeCalls.Should().Be(2);
    }

    [Fact]
    public async Task Explicit_foreign_handle_is_rejected_while_queued()
    {
        var provider = new M24Provider();
        var (coordinator, _) = CreateCoordinator(provider);
        var accepted = await coordinator.AcceptAsync(new DelegationCallerScope("caller"), Request("foreign-queued"));
        var queued = await coordinator.GetAsync(accepted.DelegationId);
        var foreign = M24Provider.CreateForeignHandle();
        var request = new ExternalOperationCancelRequest(foreign, "cancel-foreign", "Stop now.");

        var act = () => coordinator.CancelAsync(accepted.DelegationId, queued.Progress.Revision, request).AsTask();

        await act.Should().ThrowAsync<InvalidOperationException>();
        provider.TotalCalls.Should().Be(0);
    }

    [Fact]
    public async Task Convenience_cancel_uses_the_public_canonical_key_and_prose_rules()
    {
        var provider = new M24Provider();
        var (coordinator, _) = CreateCoordinator(provider);
        var accepted = await coordinator.AcceptAsync(new DelegationCallerScope("caller"), Request("canonical-cancel"));
        var queued = await coordinator.GetAsync(accepted.DelegationId);

        var badKey = () => coordinator.CancelAsync(
            accepted.DelegationId,
            queued.Progress.Revision,
            " cancel-key",
            "Stop now.").AsTask();
        var badControl = () => coordinator.CancelAsync(
            accepted.DelegationId,
            queued.Progress.Revision,
            "cancel-key",
            "Stop\u0001now.").AsTask();

        await badKey.Should().ThrowAsync<ArgumentException>();
        await badControl.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Convenience_cancel_normalizes_prose_before_building_the_public_request()
    {
        var provider = new M24Provider { CancellationDisposition = ExternalOperationCancellationDisposition.ConfirmedCancelled };
        var (coordinator, _) = CreateCoordinator(provider);
        var accepted = await coordinator.AcceptAsync(new DelegationCallerScope("caller"), Request("normalize-cancel"));
        var queued = await coordinator.GetAsync(accepted.DelegationId);
        var running = await coordinator.PumpAsync(accepted.DelegationId, queued.Progress.Revision);

        await coordinator.CancelAsync(
            accepted.DelegationId,
            running.Progress.Revision,
            "cancel-normalize",
            "  Stop\r\nnow.  ");

        provider.SeenCancelRequests.Should().ContainSingle();
        provider.SeenCancelRequests[0].CancellationKey.Should().Be("cancel-normalize");
        provider.SeenCancelRequests[0].Reason.Should().Be("Stop\nnow.");
    }

    [Theory]
    [InlineData(ExternalOperationCancellationDisposition.Requested, ExternalOperationState.Cancelled, DelegationState.Cancelled)]
    [InlineData(ExternalOperationCancellationDisposition.Requested, ExternalOperationState.Succeeded, DelegationState.Completed)]
    [InlineData(ExternalOperationCancellationDisposition.Rejected, ExternalOperationState.Cancelled, DelegationState.Cancelled)]
    [InlineData(ExternalOperationCancellationDisposition.Unknown, ExternalOperationState.Succeeded, DelegationState.Completed)]
    public async Task Nonterminal_cancellation_receipts_do_not_publish_false_terminal_state_and_eventual_observation_wins(
        ExternalOperationCancellationDisposition disposition,
        ExternalOperationState observedState,
        DelegationState expectedTerminalState)
    {
        var provider = new M24Provider
        {
            CancellationDisposition = disposition,
            PostCancellationObservationState = observedState,
        };
        var (coordinator, _) = CreateCoordinator(provider);
        var accepted = await coordinator.AcceptAsync(new DelegationCallerScope("caller"), Request($"cancel-{disposition}"));
        var queued = await coordinator.GetAsync(accepted.DelegationId);
        var running = await coordinator.PumpAsync(accepted.DelegationId, queued.Progress.Revision);

        var requested = await coordinator.CancelAsync(
            accepted.DelegationId,
            running.Progress.Revision,
            $"cancel-{disposition}",
            "Stop now.");

        requested.Progress.State.Should().Be(DelegationState.Running);
        requested.Result.Should().BeNull();
        provider.CancelCalls.Should().Be(1);

        var observed = await coordinator.PumpAsync(accepted.DelegationId, requested.Progress.Revision);
        if (expectedTerminalState == DelegationState.Completed)
        {
            observed.Progress.State.Should().Be(DelegationState.Running);
            var completed = await coordinator.PumpAsync(accepted.DelegationId, observed.Progress.Revision);
            completed.Progress.State.Should().Be(DelegationState.Completed);
            completed.Result.Should().NotBeNull();
            provider.GetResultCalls.Should().Be(1);
        }
        else
        {
            observed.Progress.State.Should().Be(expectedTerminalState);
            observed.Result.Should().NotBeNull();
            provider.GetResultCalls.Should().Be(0);
        }

        provider.CancelCalls.Should().Be(1);
        provider.ObserveCalls.Should().Be(1);
    }

    [Theory]
    [InlineData(ExternalOperationState.Cancelled, DelegationState.Cancelled)]
    [InlineData(ExternalOperationState.Succeeded, DelegationState.Completed)]
    [InlineData(ExternalOperationState.Failed, DelegationState.Failed)]
    public async Task Already_terminal_cancellation_receipt_uses_valid_provider_state_without_false_cancel(
        ExternalOperationState providerTerminalState,
        DelegationState expectedTerminalState)
    {
        var provider = new M24Provider
        {
            CancellationDisposition = ExternalOperationCancellationDisposition.AlreadyTerminal,
            AlreadyTerminalState = providerTerminalState,
        };
        var (coordinator, _) = CreateCoordinator(provider);
        var accepted = await coordinator.AcceptAsync(new DelegationCallerScope("caller"), Request($"already-{providerTerminalState}"));
        var queued = await coordinator.GetAsync(accepted.DelegationId);
        var running = await coordinator.PumpAsync(accepted.DelegationId, queued.Progress.Revision);

        var terminalOrRunning = await coordinator.CancelAsync(
            accepted.DelegationId,
            running.Progress.Revision,
            $"already-{providerTerminalState}",
            "Stop now.");

        if (expectedTerminalState == DelegationState.Completed)
        {
            terminalOrRunning.Progress.State.Should().Be(DelegationState.Running);
            var completed = await coordinator.PumpAsync(accepted.DelegationId, terminalOrRunning.Progress.Revision);
            completed.Progress.State.Should().Be(DelegationState.Completed);
            provider.GetResultCalls.Should().Be(1);
        }
        else
        {
            terminalOrRunning.Progress.State.Should().Be(expectedTerminalState);
            terminalOrRunning.Result.Should().NotBeNull();
            provider.GetResultCalls.Should().Be(0);
        }

        provider.CancelCalls.Should().Be(1);
        provider.ObserveCalls.Should().Be(0);
    }

    [Fact]
    public async Task Ambiguous_start_with_cancellation_intent_reuses_exact_start_identity_and_cancels_recovered_handle()
    {
        var provider = new M24Provider
        {
            ThrowStartBeforeCaptureOnce = true,
            CancellationDisposition = ExternalOperationCancellationDisposition.ConfirmedCancelled,
        };
        var (coordinator, _) = CreateCoordinator(provider);
        var accepted = await coordinator.AcceptAsync(new DelegationCallerScope("caller"), Request("cancel-before-handle"));
        var queued = await coordinator.GetAsync(accepted.DelegationId);
        var first = await coordinator.PumpAsync(accepted.DelegationId, queued.Progress.Revision);

        var intent = await coordinator.CancelAsync(
            accepted.DelegationId,
            first.Progress.Revision,
            "cancel-before-handle-1",
            "Stop before the provider handle is known.");
        intent.Progress.State.Should().Be(DelegationState.Running);
        intent.Result.Should().BeNull();

        var cancelled = await coordinator.PumpAsync(accepted.DelegationId, intent.Progress.Revision);

        cancelled.Progress.State.Should().Be(DelegationState.Cancelled);
        provider.StartCalls.Should().Be(2);
        provider.SeenStartRequests.Should().HaveCount(2);
        provider.SeenStartRequests[0].Identity.Should().Be(provider.SeenStartRequests[1].Identity);
        provider.SeenStartRequests[0].Correlation.Should().Be(provider.SeenStartRequests[1].Correlation);
        provider.CancelCalls.Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_cancel_and_pump_are_revision_fenced_to_one_provider_side_effect()
    {
        var provider = new M24Provider { PostCancellationObservationState = ExternalOperationState.Cancelled };
        var (coordinator, _) = CreateCoordinator(provider);
        var accepted = await coordinator.AcceptAsync(new DelegationCallerScope("caller"), Request("race-cancel-pump"));
        var queued = await coordinator.GetAsync(accepted.DelegationId);
        var running = await coordinator.PumpAsync(accepted.DelegationId, queued.Progress.Revision);

        var cancel = coordinator.CancelAsync(accepted.DelegationId, running.Progress.Revision, "race-cancel-1", "Stop now.").AsTask();
        var pump = coordinator.PumpAsync(accepted.DelegationId, running.Progress.Revision).AsTask();
        var outcomes = await Task.WhenAll(
            ObserveOutcomeAsync(cancel),
            ObserveOutcomeAsync(pump));

        outcomes.Count(outcome => outcome.IsStale).Should().Be(1);
        provider.CancelCalls.Should().BeLessThanOrEqualTo(1);
        provider.ObserveCalls.Should().BeLessThanOrEqualTo(1);
        (provider.CancelCalls + provider.ObserveCalls).Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_cancel_and_resume_are_revision_fenced_to_one_provider_side_effect()
    {
        var provider = new M24Provider { WaitingBeforeResume = true };
        var (coordinator, _) = CreateCoordinator(provider);
        var accepted = await coordinator.AcceptAsync(new DelegationCallerScope("caller"), Request("race-cancel-resume"));
        var queued = await coordinator.GetAsync(accepted.DelegationId);
        var running = await coordinator.PumpAsync(accepted.DelegationId, queued.Progress.Revision);
        var waiting = await coordinator.PumpAsync(accepted.DelegationId, running.Progress.Revision);
        var supervisor = new SupervisorIdentity("m24", "operator");
        await coordinator.ActivateCheckpointAsync(accepted.DelegationId, supervisor);
        var intervention = new SupervisorIntervention(accepted.DelegationId, waiting.Progress.Checkpoint!.CheckpointId, "race-resume-1", waiting.Progress.Revision, new SupervisorAction.Approve());

        var cancel = coordinator.CancelAsync(accepted.DelegationId, waiting.Progress.Revision, "race-cancel-1", "Stop now.").AsTask();
        var resume = coordinator.ApplyInterventionAsync(supervisor, intervention).AsTask();
        var outcomes = await Task.WhenAll(
            ObserveOutcomeAsync(cancel),
            ObserveOutcomeAsync(resume));

        outcomes.Count(outcome => outcome.IsStale).Should().Be(1);
        (provider.CancelCalls + provider.ResumeCalls).Should().Be(1);
    }

    [Fact]
    public async Task Terminal_cancel_and_resume_replays_do_not_call_provider_or_mutate_result()
    {
        var provider = new M24Provider { CancellationDisposition = ExternalOperationCancellationDisposition.ConfirmedCancelled };
        var (coordinator, _) = CreateCoordinator(provider);
        var accepted = await coordinator.AcceptAsync(new DelegationCallerScope("caller"), Request("terminal-controls"));
        var queued = await coordinator.GetAsync(accepted.DelegationId);
        var running = await coordinator.PumpAsync(accepted.DelegationId, queued.Progress.Revision);
        var cancelled = await coordinator.CancelAsync(accepted.DelegationId, running.Progress.Revision, "terminal-cancel-1", "Stop now.");

        var cancelReplay = await coordinator.CancelAsync(accepted.DelegationId, 0, "terminal-cancel-1", "Stop now.");
        var directResume = () => coordinator.ResumeAsync(accepted.DelegationId, cancelled.Progress.Revision, "terminal-resume-1").AsTask();
        await directResume.Should().ThrowAsync<InvalidOperationException>();

        cancelReplay.Should().BeSameAs(cancelled);
        provider.CancelCalls.Should().Be(1);
        provider.ResumeCalls.Should().Be(0);
    }

    private static async Task<(bool IsStale, DelegationExecutionSnapshot? Snapshot)> ObserveOutcomeAsync(
        Task<DelegationExecutionSnapshot> operation)
    {
        try
        {
            return (false, await operation);
        }
        catch (DelegationExecutionStaleException)
        {
            return (true, null);
        }
        catch (SupervisorInterventionRejectedException)
        {
            // The serialized winner may transition the checkpoint before the
            // competing intervention reaches its pre-acceptance fence.
            return (true, null);
        }
    }

    [Fact]
    public async Task Pending_cancellation_uses_a_bounded_safety_observation_ceiling_after_worker_budget()
    {
        var provider = new M24Provider { CancellationDisposition = ExternalOperationCancellationDisposition.Requested };
        var (coordinator, _) = CreateCoordinator(provider);
        var accepted = await coordinator.AcceptAsync(new DelegationCallerScope("caller"), Request("cancel-safety", 1, 0));
        var queued = await coordinator.GetAsync(accepted.DelegationId);
        var running = await coordinator.PumpAsync(accepted.DelegationId, queued.Progress.Revision);
        var requested = await coordinator.CancelAsync(accepted.DelegationId, running.Progress.Revision, "cancel-safety-1", "Stop now.");

        var current = requested;
        for (var i = 0; i < InMemoryDelegationCoordinator.CancellationSafetyCallLimit + 3; i++)
        {
            current = await coordinator.PumpAsync(accepted.DelegationId, current.Progress.Revision);
        }

        current.Progress.State.Should().Be(DelegationState.NeedsSupervisor);
        current.Result!.UnresolvedConcerns.Should().ContainSingle(concern =>
            concern.Contains("cancellation", StringComparison.OrdinalIgnoreCase));
        provider.ObserveCalls.Should().Be(InMemoryDelegationCoordinator.CancellationSafetyCallLimit - 1);
        provider.CancelCalls.Should().Be(1);
        current.Progress.WorkerCalls.Should().Be(provider.StartCalls + provider.ObserveCalls + provider.CancelCalls);
    }

    private static (InMemoryDelegationCoordinator Coordinator, InMemoryExternalOperationHandleCaptureRegistry Captures) CreateCoordinator(M24Provider provider)
    {
        var descriptor = new ProviderDescriptor("m24-provider", [new CapabilityDescriptor("agent.execute", 1)]);
        var providers = new InMemoryProviderRegistry();
        providers.Register(descriptor);
        var adapters = new InMemoryExternalOperationProviderCatalog();
        adapters.Register(descriptor, provider);
        var captures = new InMemoryExternalOperationHandleCaptureRegistry();
        return (new InMemoryDelegationCoordinator(
            new InMemoryDelegationAcceptanceRegistry(),
            new InMemoryWorkflowPlanResolver(),
            providers,
            adapters,
            new SimingExternalOperationSemanticFingerprintVerifier(),
            captures,
            now: () => Start), captures);
    }

    private static DelegationRequest Request(string key, int maximumWorkerCalls = 4, int maximumRetries = 2) => new(
        key,
        "Implement the objective",
        new WorkspaceReference("local", "workspace", "revision"),
        ["The result is correct"],
        [],
        new DelegationBudget(maximumWorkerCalls, maximumRetries));

    private sealed class M24Provider : IExternalOperationProvider
    {
        private ExternalOperationHandle? handle;
        public List<ExternalOperationStartRequest> SeenStartRequests { get; } = [];
        public List<ExternalOperationCancelRequest> SeenCancelRequests { get; } = [];
        public int CancelCalls { get; private set; }
        public int StartCalls { get; private set; }
        public int GetResultCalls { get; private set; }
        public int ResumeCalls { get; private set; }
        public int ObserveCalls { get; private set; }
        public int TotalCalls => CancelCalls + ResumeCalls;
        public bool WaitingBeforeResume { get; init; }
        public bool LoseResumeReceiptAfterCapture { get; init; }
        public bool FailResumeWithoutCapture { get; init; }
        public bool ThrowStartBeforeCaptureOnce { get; init; }
        public ExternalOperationState PostCancellationObservationState { get; init; } = ExternalOperationState.Running;
        public ExternalOperationState AlreadyTerminalState { get; init; } = ExternalOperationState.Cancelled;
        public ExternalOperationCancellationDisposition CancellationDisposition { get; init; } = ExternalOperationCancellationDisposition.Requested;

        public ValueTask<ExternalOperationStartReceipt> StartAsync(ExternalOperationStartRequest request, IExternalOperationHandleCaptureSink handleSink, CancellationToken cancellationToken = default)
        {
            StartCalls++;
            SeenStartRequests.Add(request);
            if (ThrowStartBeforeCaptureOnce && StartCalls == 1)
            {
                throw new ExternalOperationProviderException(new ExternalOperationFailure(
                    ExternalOperationFailureKind.Transport,
                    "v1.start-transport",
                    "The start transport was interrupted.",
                    retryable: true));
            }

            handle = CreateHandle(request, "handle-1");
            return StartCoreAsync(request, handleSink, cancellationToken);
        }

        private async ValueTask<ExternalOperationStartReceipt> StartCoreAsync(ExternalOperationStartRequest request, IExternalOperationHandleCaptureSink sink, CancellationToken token)
        {
            await sink.CaptureAsync(new ExternalOperationHandleCapture(handle!, Start.AddMinutes(1)), token);
            return new ExternalOperationStartReceipt(request.Identity, handle!, ExternalOperationStartDisposition.Created, ExternalOperationState.Running, Start.AddMinutes(1));
        }

        public ValueTask<ExternalOperationObservation> ObserveAsync(ExternalOperationHandle operationHandle, CancellationToken cancellationToken = default)
        {
            ObserveCalls++;
            var state = WaitingBeforeResume && ResumeCalls == 0
                ? ExternalOperationState.Waiting
                : CancelCalls > 0
                    ? PostCancellationObservationState
                    : ExternalOperationState.Running;
            var failure = state is ExternalOperationState.Failed
                or ExternalOperationState.TimedOut
                or ExternalOperationState.Rejected
                ? new ExternalOperationFailure(
                    state == ExternalOperationState.Rejected
                        ? ExternalOperationFailureKind.Rejection
                        : ExternalOperationFailureKind.Remote,
                    "v1.observe-terminal",
                    "The provider reported a terminal failure.",
                    retryable: false)
                : null;
            return ValueTask.FromResult(new ExternalOperationObservation(
                operationHandle,
                ObserveCalls,
                state,
                Start.AddMinutes(2 + ObserveCalls),
                failure: failure,
                resultAvailable: state is ExternalOperationState.Succeeded
                    or ExternalOperationState.Failed
                    or ExternalOperationState.Cancelled));
        }

        public ValueTask<ExternalOperationResult> GetResultAsync(ExternalOperationHandle operationHandle, CancellationToken cancellationToken = default)
        {
            GetResultCalls++;
            return ValueTask.FromResult(new ExternalOperationResult(
                operationHandle,
                ExternalOperationState.Succeeded,
                Start.AddMinutes(10),
                "The operation completed.",
                []));
        }

        public ValueTask<ExternalOperationCancellationReceipt> CancelAsync(ExternalOperationCancelRequest request, CancellationToken cancellationToken = default)
        {
            CancelCalls++;
            SeenCancelRequests.Add(request);
            var state = CancellationDisposition switch
            {
                ExternalOperationCancellationDisposition.Requested => ExternalOperationState.CancellationRequested,
                ExternalOperationCancellationDisposition.ConfirmedCancelled => ExternalOperationState.Cancelled,
                ExternalOperationCancellationDisposition.AlreadyTerminal => AlreadyTerminalState,
                ExternalOperationCancellationDisposition.Rejected => ExternalOperationState.Running,
                ExternalOperationCancellationDisposition.Unknown => ExternalOperationState.Unknown,
                _ => throw new InvalidOperationException(),
            };
            var failure = CancellationDisposition is ExternalOperationCancellationDisposition.Rejected
                or ExternalOperationCancellationDisposition.Unknown
                ? new ExternalOperationFailure(
                    ExternalOperationFailureKind.Cancellation,
                    "v1.cancel-ambiguous",
                    "The provider could not durably confirm cancellation.",
                    retryable: true)
                : null;
            return ValueTask.FromResult(new ExternalOperationCancellationReceipt(
                request.Handle,
                request.CancellationKey,
                CancellationDisposition,
                state,
                Start.AddMinutes(4),
                failure));
        }

        public async ValueTask<ExternalOperationResumeReceipt> ResumeAsync(ExternalOperationResumeRequest request, IExternalOperationHandleCaptureSink handleSink, CancellationToken cancellationToken = default)
        {
            ResumeCalls++;
            if (FailResumeWithoutCapture)
            {
                throw new InvalidOperationException("resume response lost before capture");
            }

            var previous = request.Handle;
            handle = new ExternalOperationHandle(
                previous.Provider,
                "handle-rotated",
                previous.ProtocolVersion,
                previous.Correlation);
            await handleSink.CaptureAsync(new ExternalOperationHandleCapture(handle, Start.AddMinutes(5)), cancellationToken);
            if (LoseResumeReceiptAfterCapture)
            {
                throw new InvalidOperationException("resume response lost after capture");
            }

            return new ExternalOperationResumeReceipt(previous, request.ResumeKey, handle, ExternalOperationStartDisposition.Existing, ExternalOperationState.Running, Start.AddMinutes(5));
        }

        public static ExternalOperationHandle CreateForeignHandle()
        {
            var agent = new ExternalAgentReference("foreign-provider", "foreign-provider", "qingniao-inmemory-v1");
            var correlation = new ExternalOperationCorrelation(
                new DelegationId(Guid.Parse("10000000-0000-0000-0000-000000000001")),
                new WorkflowRunExecutionReference("foreign-workflow", "foreign-run", "epoch-1"),
                new StructuralNodeReference("implement"),
                new NodeGenerationId(Guid.Parse("20000000-0000-0000-0000-000000000001")),
                "attempt-1",
                agent,
                new ExternalTaskReference(agent.Provider, "foreign-task"));
            return new ExternalOperationHandle(agent.Provider, "foreign-handle", agent.ProtocolVersion, correlation);
        }

        private static ExternalOperationHandle CreateHandle(ExternalOperationStartRequest request, string value) => new(
            request.Correlation.Agent.Provider,
            value,
            request.Correlation.Agent.ProtocolVersion,
            new ExternalOperationCorrelation(
                request.Correlation.DelegationId,
                request.Correlation.WorkflowRun,
                request.Correlation.StructuralNode,
                request.Correlation.NodeGeneration,
                request.Correlation.ExecutionAttemptId,
                request.Correlation.Agent,
                new ExternalTaskReference(request.Correlation.Agent.Provider, value)));
    }
}
