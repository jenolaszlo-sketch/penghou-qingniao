using FluentAssertions;

namespace Penghou.Qingniao.Tests;

/// <summary>
/// Provider-neutral contract conformance at the coordinator seam: the
/// observation states a provider may report map to the documented delegation
/// outcomes. Guards the QH-06 seam where a Codex Transport-kind terminal
/// surfaces as a non-retryable <see cref="ExternalOperationState.Unknown"/>.
/// </summary>
public sealed class ProviderObservationConformanceTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Non_retryable_unknown_observation_escalates_instead_of_exhausting_budget()
    {
        var provider = new UnknownObserveProvider(retryable: false);
        var coordinator = CreateCoordinator(provider);
        var ct = TestContext.Current.CancellationToken;

        var acceptance = await coordinator.AcceptAsync(Caller(), Request(), ct);
        var queued = await coordinator.GetAsync(acceptance.DelegationId, ct);
        var running = await coordinator.PumpAsync(acceptance.DelegationId, queued.Progress.Revision, ct);
        running.Progress.State.Should().Be(DelegationState.Running);

        var terminal = await coordinator.PumpAsync(acceptance.DelegationId, running.Progress.Revision, ct);

        // Escalated on the first ambiguous observation: the worker-call budget
        // is not spent on no-op re-observations.
        terminal.Progress.State.Should().Be(DelegationState.NeedsSupervisor);
        provider.ObserveCalls.Should().Be(1);
        terminal.Progress.WorkerCalls.Should().BeLessThan(5);
        terminal.Result!.UnresolvedConcerns.Should().ContainSingle()
            .Which.Should().Contain("local.pump-failed");
    }

    private static InMemoryDelegationCoordinator CreateCoordinator(UnknownObserveProvider provider)
    {
        var descriptor = new ProviderDescriptor("fake-provider", [new CapabilityDescriptor("agent.execute", 1)]);
        var providers = new InMemoryProviderRegistry();
        providers.Register(descriptor);
        var adapters = new InMemoryExternalOperationProviderCatalog();
        adapters.Register(descriptor, provider);
        return new InMemoryDelegationCoordinator(
            new InMemoryDelegationAcceptanceRegistry(),
            null,
            providers,
            adapters,
            new LocalSemanticFingerprintVerifier(),
            new InMemoryExternalOperationHandleCaptureRegistry(),
            null,
            now: () => Start);
    }

    private static DelegationCallerScope Caller() => new("caller-1");

    private static DelegationRequest Request() => new(
        "conformance-unknown",
        "objective",
        "fake-provider",
        new WorkspaceReference("local", "workspace", "revision"),
        ["done"],
        [],
        new DelegationBudget(MaximumWorkerCalls: 5, MaximumRetries: 1));

    private sealed class UnknownObserveProvider(bool retryable) : IExternalOperationProvider
    {
        private static readonly DateTimeOffset AcceptedAt = Start.AddMinutes(1);

        public int ObserveCalls { get; private set; }

        public async ValueTask<ExternalOperationStartReceipt> StartAsync(
            ExternalOperationStartRequest request,
            IExternalOperationHandleCaptureSink handleSink,
            CancellationToken cancellationToken = default)
        {
            var correlation = new ExternalOperationCorrelation(
                request.Correlation.DelegationId,
                request.Correlation.WorkflowRun,
                request.Correlation.StructuralNode,
                request.Correlation.NodeGeneration,
                request.Correlation.ExecutionAttemptId,
                request.Correlation.Agent,
                new ExternalTaskReference(request.Correlation.Agent.Provider, "task-1"));
            var handle = new ExternalOperationHandle(
                request.Correlation.Agent.Provider,
                "handle-1",
                request.Correlation.Agent.ProtocolVersion,
                correlation);
            await handleSink.CaptureAsync(
                new ExternalOperationHandleCapture(handle, AcceptedAt), cancellationToken);
            return new ExternalOperationStartReceipt(
                request.Identity, handle, ExternalOperationStartDisposition.Created,
                ExternalOperationState.Running, AcceptedAt);
        }

        public ValueTask<ExternalOperationObservation> ObserveAsync(
            ExternalOperationHandle operationHandle,
            CancellationToken cancellationToken = default)
        {
            ObserveCalls++;
            var failure = new ExternalOperationFailure(
                ExternalOperationFailureKind.Transport,
                "local.pump-failed",
                "The local output pump failed; the child's outcome is unknown.",
                retryable);
            return ValueTask.FromResult(new ExternalOperationObservation(
                operationHandle,
                ObserveCalls,
                ExternalOperationState.Unknown,
                AcceptedAt.AddMinutes(ObserveCalls),
                failure: failure,
                resultAvailable: false));
        }

        public ValueTask<ExternalOperationResult> GetResultAsync(
            ExternalOperationHandle operationHandle,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<ExternalOperationCancellationReceipt> CancelAsync(
            ExternalOperationCancelRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<ExternalOperationResumeReceipt> ResumeAsync(
            ExternalOperationResumeRequest request,
            IExternalOperationHandleCaptureSink handleSink,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
