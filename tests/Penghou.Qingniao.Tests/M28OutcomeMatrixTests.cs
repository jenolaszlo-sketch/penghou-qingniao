#pragma warning disable xUnit1051

using FluentAssertions;

namespace Penghou.Qingniao.Tests;

/// <summary>Parameterized proof of the M2.8 terminal-outcome matrix.</summary>
public sealed class M28OutcomeMatrixTests
{
    private static readonly DateTimeOffset Start = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(MatrixMode.ClassifiedStart)]
    [InlineData(MatrixMode.UnclassifiedStart)]
    [InlineData(MatrixMode.ClassifiedObserve)]
    [InlineData(MatrixMode.UnclassifiedObserve)]
    [InlineData(MatrixMode.ClassifiedResult)]
    [InlineData(MatrixMode.UnclassifiedResult)]
    public async Task Provider_failures_are_terminal_and_sanitized_at_each_phase(MatrixMode mode)
    {
        var (coordinator, provider) = Create(mode);
        var accepted = await coordinator.AcceptAsync(Caller(), Request(mode.ToString()));
        var queued = await coordinator.GetAsync(accepted.DelegationId);
        var current = await coordinator.PumpAsync(accepted.DelegationId, queued.Progress.Revision);

        if (mode is MatrixMode.ClassifiedStart or MatrixMode.UnclassifiedStart)
        {
            current.Progress.State.Should().Be(DelegationState.Failed);
        }
        else
        {
            current = await coordinator.PumpAsync(accepted.DelegationId, current.Progress.Revision);
            if (mode is MatrixMode.ClassifiedObserve or MatrixMode.UnclassifiedObserve)
            {
                current.Progress.State.Should().Be(DelegationState.Failed);
            }
            else
            {
                current = await coordinator.PumpAsync(accepted.DelegationId, current.Progress.Revision);
                current = await coordinator.PumpAsync(accepted.DelegationId, current.Progress.Revision);
                current.Progress.State.Should().Be(DelegationState.Failed);
            }
        }

        current.Result.Should().NotBeNull();
        current.Result!.Summary.Should().NotContain("secret-provider-detail");
        current.Result.UnresolvedConcerns.Should().NotContain(concern => concern.Contains("secret-provider-detail", StringComparison.Ordinal));
        provider.StartCalls.Should().Be(1);
    }

    [Theory]
    [InlineData(MatrixMode.RejectedObserve)]
    [InlineData(MatrixMode.RejectedResult)]
    public async Task Semantic_provider_rejection_has_a_stable_failed_reason(MatrixMode mode)
    {
        var (coordinator, _) = Create(mode);
        var accepted = await coordinator.AcceptAsync(Caller(), Request(mode.ToString()));
        var queued = await coordinator.GetAsync(accepted.DelegationId);
        var current = await coordinator.PumpAsync(accepted.DelegationId, queued.Progress.Revision);
        current = await coordinator.PumpAsync(accepted.DelegationId, current.Progress.Revision);
        if (mode == MatrixMode.RejectedResult)
        {
            current = await coordinator.PumpAsync(accepted.DelegationId, current.Progress.Revision);
            current = await coordinator.PumpAsync(accepted.DelegationId, current.Progress.Revision);
        }

        current.Progress.State.Should().Be(DelegationState.Failed);
        current.Result!.Summary.ToLowerInvariant().Should().Contain("rejected");
        current.Result.Summary.Should().NotContain("secret-provider-detail");
    }

    [Theory]
    [InlineData(ExternalOperationCancellationDisposition.ConfirmedCancelled, DelegationState.Cancelled)]
    [InlineData(ExternalOperationCancellationDisposition.Requested, DelegationState.NeedsSupervisor)]
    [InlineData(ExternalOperationCancellationDisposition.Rejected, DelegationState.NeedsSupervisor)]
    [InlineData(ExternalOperationCancellationDisposition.Unknown, DelegationState.NeedsSupervisor)]
    public async Task Cancellation_dispositions_reconcile_or_escalate_without_hanging(
        ExternalOperationCancellationDisposition disposition,
        DelegationState expected)
    {
        var (coordinator, provider) = Create(disposition switch
        {
            ExternalOperationCancellationDisposition.ConfirmedCancelled => MatrixMode.CancelConfirmed,
            ExternalOperationCancellationDisposition.Requested => MatrixMode.CancelRequested,
            ExternalOperationCancellationDisposition.Rejected => MatrixMode.CancelRejected,
            _ => MatrixMode.CancelUnknown,
        });
        var accepted = await coordinator.AcceptAsync(Caller(), Request(disposition.ToString()));
        var queued = await coordinator.GetAsync(accepted.DelegationId);
        var running = await coordinator.PumpAsync(accepted.DelegationId, queued.Progress.Revision);
        var observed = await coordinator.PumpAsync(accepted.DelegationId, running.Progress.Revision);
        var requested = await coordinator.CancelAsync(
            accepted.DelegationId,
            observed.Progress.Revision,
            "m28-cancel",
            "stop");

        requested.Progress.State.Should().Be(expected == DelegationState.Cancelled
            ? DelegationState.Cancelled
            : DelegationState.Running);
        var current = requested;
        for (var index = 0; index < InMemoryDelegationCoordinator.CancellationSafetyCallLimit + 2
            && !DelegationLifecycle.IsTerminal(current.Progress.State); index++)
        {
            current = await coordinator.PumpAsync(accepted.DelegationId, current.Progress.Revision);
        }

        current.Progress.State.Should().Be(expected);
        provider.CancelCalls.Should().Be(1);
        if (expected == DelegationState.NeedsSupervisor)
        {
            current.Progress.WorkerCalls.Should().BeGreaterThan(observed.Progress.WorkerCalls);
        }
    }

    [Fact]
    public async Task Worker_and_retry_ceilings_are_budget_outcomes_with_accounting()
    {
        var (workerCoordinator, workerProvider) = Create(MatrixMode.Success);
        var workerAccepted = await workerCoordinator.AcceptAsync(Caller(), Request("worker-budget", maximumWorkerCalls: 1));
        var workerQueued = await workerCoordinator.GetAsync(workerAccepted.DelegationId);
        var workerRunning = await workerCoordinator.PumpAsync(workerAccepted.DelegationId, workerQueued.Progress.Revision);
        var workerExceeded = await workerCoordinator.PumpAsync(workerAccepted.DelegationId, workerRunning.Progress.Revision);
        workerExceeded.Progress.State.Should().Be(DelegationState.BudgetExceeded);
        workerExceeded.Result!.BudgetExceeded.Should().NotBeNull();
        workerExceeded.Result.BudgetExceeded!.Consumed.Value.Should().BeGreaterThan(workerExceeded.Result.BudgetExceeded.Limit.Value);
        workerExceeded.Result.BudgetExceeded.ActualConsumed.Value.Should().Be(1);
        workerExceeded.Result.BudgetExceeded.RefusedCharge!.Amount.Value.Should().Be(1);
        workerExceeded.Result.BudgetExceeded.TriggeringReceiptId.Should().BeNull();
        workerProvider.ObserveCalls.Should().Be(0);

        var (retryCoordinator, retryProvider) = Create(MatrixMode.RetryableStart);
        var retryAccepted = await retryCoordinator.AcceptAsync(Caller(), Request("retry-budget", maximumRetries: 0));
        var retryQueued = await retryCoordinator.GetAsync(retryAccepted.DelegationId);
        var retryRunning = await retryCoordinator.PumpAsync(retryAccepted.DelegationId, retryQueued.Progress.Revision);
        var retryExceeded = await retryCoordinator.PumpAsync(retryAccepted.DelegationId, retryRunning.Progress.Revision);
        retryExceeded.Progress.State.Should().Be(DelegationState.BudgetExceeded);
        retryExceeded.Result!.BudgetExceeded!.Charge.Dimension.Should().Be("retries");
        retryExceeded.Result.BudgetExceeded.ActualConsumed.Value.Should().Be(0);
        retryExceeded.Result.BudgetExceeded.RefusedCharge!.Amount.Value.Should().Be(1);
        retryExceeded.Result.BudgetExceeded.TriggeringReceiptId.Should().BeNull();
        retryProvider.StartCalls.Should().Be(1);
    }

    [Fact]
    public async Task Waiting_is_nonterminal_and_checkpoint_replay_is_exact()
    {
        var (coordinator, _) = Create(MatrixMode.Waiting);
        var accepted = await coordinator.AcceptAsync(Caller(), Request("waiting"));
        var queued = await coordinator.GetAsync(accepted.DelegationId);
        var running = await coordinator.PumpAsync(accepted.DelegationId, queued.Progress.Revision);
        var waiting = await coordinator.PumpAsync(accepted.DelegationId, running.Progress.Revision);

        waiting.Progress.State.Should().Be(DelegationState.WaitingForSupervisor);
        waiting.Progress.Checkpoint.Should().NotBeNull();
        var replay = await coordinator.PumpAsync(accepted.DelegationId, waiting.Progress.Revision);
        replay.Should().BeSameAs(waiting);
        replay.Result.Should().BeNull();
    }

    [Fact]
    public async Task Missing_adapter_is_an_honest_supervisory_outcome()
    {
        var descriptor = new ProviderDescriptor("m28-missing", [new CapabilityDescriptor("agent.execute", 1)]);
        var providers = new InMemoryProviderRegistry();
        providers.Register(descriptor);
        var coordinator = new InMemoryDelegationCoordinator(
            new InMemoryDelegationAcceptanceRegistry(),
            new InMemoryWorkflowPlanResolver(),
            providers,
            new InMemoryExternalOperationProviderCatalog(),
            new SimingExternalOperationSemanticFingerprintVerifier(),
            now: () => Start);
        var accepted = await coordinator.AcceptAsync(Caller(), Request("missing-adapter"));
        var queued = await coordinator.GetAsync(accepted.DelegationId);
        var terminal = await coordinator.PumpAsync(accepted.DelegationId, queued.Progress.Revision);

        terminal.Progress.State.Should().Be(DelegationState.NeedsSupervisor);
        terminal.Result!.Summary.Should().Contain("adapter");
    }

    [Fact]
    public async Task Terminal_result_replay_preserves_the_exact_immutable_pair()
    {
        var (coordinator, _) = Create(MatrixMode.Success);
        var accepted = await coordinator.AcceptAsync(Caller(), Request("replay"));
        var queued = await coordinator.GetAsync(accepted.DelegationId);
        var running = await coordinator.PumpAsync(accepted.DelegationId, queued.Progress.Revision);
        var observed = await coordinator.PumpAsync(accepted.DelegationId, running.Progress.Revision);
        var terminal = await coordinator.PumpAsync(accepted.DelegationId, observed.Progress.Revision);
        var replay = await coordinator.PumpAsync(accepted.DelegationId, terminal.Progress.Revision);

        replay.Should().BeSameAs(terminal);
        replay.Result.Should().BeSameAs(terminal.Result);
    }

    [Fact]
    public async Task Already_terminal_succeeded_cancellation_reconciles_before_result_retrieval()
    {
        var (coordinator, _) = Create(MatrixMode.CancelAlreadySucceeded);
        var accepted = await coordinator.AcceptAsync(Caller(), Request("already-succeeded"));
        var queued = await coordinator.GetAsync(accepted.DelegationId);
        var running = await coordinator.PumpAsync(accepted.DelegationId, queued.Progress.Revision);
        var observed = await coordinator.PumpAsync(accepted.DelegationId, running.Progress.Revision);
        var reconciled = await coordinator.CancelAsync(accepted.DelegationId, observed.Progress.Revision, "already-succeeded-cancel", "stop");
        var terminal = await coordinator.PumpAsync(accepted.DelegationId, reconciled.Progress.Revision);

        terminal.Progress.State.Should().Be(DelegationState.Completed);
        terminal.Result.Should().NotBeNull();
    }

    private static (InMemoryDelegationCoordinator Coordinator, MatrixProvider Provider) Create(MatrixMode mode)
    {
        var descriptor = new ProviderDescriptor("m28-provider", [new CapabilityDescriptor("agent.execute", 1)]);
        var registry = new InMemoryProviderRegistry();
        registry.Register(descriptor);
        var provider = new MatrixProvider(mode);
        var catalog = new InMemoryExternalOperationProviderCatalog();
        catalog.Register(descriptor, provider);
        return (new InMemoryDelegationCoordinator(
            new InMemoryDelegationAcceptanceRegistry(),
            new InMemoryWorkflowPlanResolver(),
            registry,
            catalog,
            new SimingExternalOperationSemanticFingerprintVerifier(),
            now: () => Start), provider);
    }

    private static DelegationCallerScope Caller() => new("m28-caller");

    private static DelegationRequest Request(string key, int maximumWorkerCalls = 8, int maximumRetries = 1) => new(
        key,
        "Implement the objective",
        new WorkspaceReference("local", "workspace", "revision"),
        ["The result is correct"],
        [],
        new DelegationBudget(maximumWorkerCalls, maximumRetries));

    public enum MatrixMode
    {
        Success,
        RejectedObserve,
        RejectedResult,
        ClassifiedStart,
        UnclassifiedStart,
        ClassifiedObserve,
        UnclassifiedObserve,
        ClassifiedResult,
        UnclassifiedResult,
        RetryableStart,
        Waiting,
        CancelConfirmed,
        CancelRequested,
        CancelRejected,
        CancelUnknown,
        CancelAlreadySucceeded,
    }

    private sealed class MatrixProvider(MatrixMode mode) : IExternalOperationProvider
    {
        private ExternalOperationHandle? captured;
        private int observationRevision;

        public int StartCalls { get; private set; }
        public int ObserveCalls { get; private set; }
        public int CancelCalls { get; private set; }

        public async ValueTask<ExternalOperationStartReceipt> StartAsync(
            ExternalOperationStartRequest request,
            IExternalOperationHandleCaptureSink handleSink,
            CancellationToken cancellationToken = default)
        {
            StartCalls++;
            if (mode == MatrixMode.ClassifiedStart)
            {
                throw Classified(ExternalOperationFailureKind.Rejection, "start.rejected");
            }

            if (mode == MatrixMode.UnclassifiedStart)
            {
                throw new InvalidOperationException("secret-provider-detail");
            }

            if (mode == MatrixMode.RetryableStart && StartCalls == 1)
            {
                throw Classified(ExternalOperationFailureKind.Transport, "start.transport", retryable: true);
            }

            var correlation = new ExternalOperationCorrelation(
                request.Correlation.DelegationId,
                request.Correlation.WorkflowRun,
                request.Correlation.StructuralNode,
                request.Correlation.NodeGeneration,
                request.Correlation.ExecutionAttemptId,
                request.Correlation.Agent,
                new ExternalTaskReference(request.Correlation.Agent.Provider, "m28-task"));
            captured = new ExternalOperationHandle("m28-provider", "m28-handle", request.Correlation.Agent.ProtocolVersion, correlation);
            await handleSink.CaptureAsync(new ExternalOperationHandleCapture(captured, Start.AddMinutes(1)), cancellationToken);
            return new ExternalOperationStartReceipt(request.Identity, captured, ExternalOperationStartDisposition.Created, ExternalOperationState.Running, Start.AddMinutes(1));
        }

        public ValueTask<ExternalOperationObservation> ObserveAsync(ExternalOperationHandle operationHandle, CancellationToken cancellationToken = default)
        {
            ObserveCalls++;
            if (mode == MatrixMode.ClassifiedObserve)
            {
                throw Classified(ExternalOperationFailureKind.Remote, "observe.remote");
            }

            if (mode == MatrixMode.UnclassifiedObserve)
            {
                throw new InvalidOperationException("secret-provider-detail");
            }

            if (mode is MatrixMode.CancelConfirmed or MatrixMode.CancelRequested or MatrixMode.CancelRejected or MatrixMode.CancelUnknown or MatrixMode.CancelAlreadySucceeded)
            {
                return ValueTask.FromResult(new ExternalOperationObservation(operationHandle, ++observationRevision, ExternalOperationState.Running, Start.AddMinutes(2)));
            }

            if (mode == MatrixMode.Waiting && ObserveCalls == 1)
            {
                return ValueTask.FromResult(new ExternalOperationObservation(operationHandle, ++observationRevision, ExternalOperationState.Waiting, Start.AddMinutes(2), "awaiting supervisor"));
            }

            if (mode == MatrixMode.RejectedObserve)
            {
                return ValueTask.FromResult(new ExternalOperationObservation(operationHandle, ++observationRevision, ExternalOperationState.Rejected, Start.AddMinutes(2), failure: new ExternalOperationFailure(ExternalOperationFailureKind.Rejection, "semantic.rejected", "secret-provider-detail", false)));
            }

            return ValueTask.FromResult(new ExternalOperationObservation(operationHandle, ++observationRevision, ExternalOperationState.Succeeded, Start.AddMinutes(2), resultAvailable: true));
        }

        public ValueTask<ExternalOperationResult> GetResultAsync(ExternalOperationHandle operationHandle, CancellationToken cancellationToken = default)
        {
            if (mode == MatrixMode.ClassifiedResult)
            {
                throw Classified(ExternalOperationFailureKind.Remote, "result.remote");
            }

            if (mode == MatrixMode.UnclassifiedResult)
            {
                throw new InvalidOperationException("secret-provider-detail");
            }

            if (mode == MatrixMode.RejectedResult)
            {
                throw Classified(ExternalOperationFailureKind.Rejection, "result.rejected");
            }

            return ValueTask.FromResult(new ExternalOperationResult(operationHandle, ExternalOperationState.Succeeded, Start.AddMinutes(3), "secret-provider-detail", []));
        }

        public ValueTask<ExternalOperationCancellationReceipt> CancelAsync(ExternalOperationCancelRequest request, CancellationToken cancellationToken = default)
        {
            CancelCalls++;
            var disposition = mode switch
            {
                MatrixMode.CancelConfirmed => ExternalOperationCancellationDisposition.ConfirmedCancelled,
                MatrixMode.CancelRejected => ExternalOperationCancellationDisposition.Rejected,
                MatrixMode.CancelUnknown => ExternalOperationCancellationDisposition.Unknown,
                MatrixMode.CancelAlreadySucceeded => ExternalOperationCancellationDisposition.AlreadyTerminal,
                _ => ExternalOperationCancellationDisposition.Requested,
            };
            var state = disposition switch
            {
                ExternalOperationCancellationDisposition.ConfirmedCancelled => ExternalOperationState.Cancelled,
                ExternalOperationCancellationDisposition.Rejected => ExternalOperationState.Running,
                ExternalOperationCancellationDisposition.Unknown => ExternalOperationState.Unknown,
                ExternalOperationCancellationDisposition.AlreadyTerminal => ExternalOperationState.Succeeded,
                _ => ExternalOperationState.CancellationRequested,
            };
            var failure = disposition is ExternalOperationCancellationDisposition.Rejected or ExternalOperationCancellationDisposition.Unknown
                ? new ExternalOperationFailure(ExternalOperationFailureKind.Cancellation, "cancel.ambiguous", "secret-provider-detail", false)
                : null;
            return ValueTask.FromResult(new ExternalOperationCancellationReceipt(request.Handle, request.CancellationKey, disposition, state, Start.AddMinutes(4), failure));
        }

        public ValueTask<ExternalOperationResumeReceipt> ResumeAsync(ExternalOperationResumeRequest request, IExternalOperationHandleCaptureSink handleSink, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        private static ExternalOperationProviderException Classified(ExternalOperationFailureKind kind, string code, bool retryable = false) =>
            new(new ExternalOperationFailure(kind, code, "secret-provider-detail", retryable));
    }
}
