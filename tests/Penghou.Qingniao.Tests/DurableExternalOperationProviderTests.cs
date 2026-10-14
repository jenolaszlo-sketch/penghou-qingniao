using FluentAssertions;

namespace Penghou.Qingniao.Tests;

public sealed class DurableExternalOperationProviderTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Start_intent_and_handle_are_durable_before_provider_callback_is_forwarded()
    {
        var journal = new TestJournal();
        var inner = new TestProvider(journal, captureOnStart: true);
        var request = CreateRequest();
        var forwarded = new RecordingSink(journal, request.Identity);
        var provider = CreateDecorator(inner, journal);

        var receipt = await provider.StartAsync(request, forwarded, TestContext.Current.CancellationToken);

        receipt.Disposition.Should().Be(ExternalOperationStartDisposition.Created);
        inner.StartCount.Should().Be(1);
        forwarded.Captures.Should().ContainSingle();
        var record = await journal.ReadAsync(request.Identity, TestContext.Current.CancellationToken);
        record!.RecoveryState.Should().Be(ExternalOperationRecoveryState.KnownObservable);
        record.Capture.Should().Be(forwarded.Captures[0]);
    }

    [Fact]
    public async Task Recovered_start_replays_known_capture_without_starting_provider_again()
    {
        var journal = new TestJournal();
        var inner = new TestProvider(journal, captureOnStart: true);
        var request = CreateRequest();
        var first = CreateDecorator(inner, journal);
        await first.StartAsync(request, new InMemoryExternalOperationHandleCaptureRegistry(), TestContext.Current.CancellationToken);

        var recoveredForward = new RecordingSink(journal, request.Identity);
        var recovered = CreateDecorator(inner, journal);
        var receipt = await recovered.StartAsync(request, recoveredForward, TestContext.Current.CancellationToken);

        receipt.Disposition.Should().Be(ExternalOperationStartDisposition.Existing);
        inner.StartCount.Should().Be(1);
        recoveredForward.Captures.Should().ContainSingle();
        recoveredForward.Captures[0].Handle.Should().Be(receipt.Handle);

        var observation = await recovered.ObserveAsync(receipt.Handle, TestContext.Current.CancellationToken);
        observation.State.Should().Be(ExternalOperationState.Running);
        (await journal.ReadAsync(request.Identity, TestContext.Current.CancellationToken))!
            .Observation.Should().Be(observation);
    }

    [Fact]
    public async Task Intent_without_handle_is_unknown_after_recovery_and_never_restarts_provider()
    {
        var journal = new TestJournal();
        var inner = new TestProvider(journal, captureOnStart: false, throwAfterStart: true);
        var request = CreateRequest();
        var first = CreateDecorator(inner, journal);
        var firstCall = () => first.StartAsync(
            request,
            new InMemoryExternalOperationHandleCaptureRegistry(),
            TestContext.Current.CancellationToken).AsTask();
        await firstCall.Should().ThrowAsync<InvalidOperationException>();

        var recovered = CreateDecorator(inner, journal);
        var secondCall = () => recovered.StartAsync(
            request,
            new InMemoryExternalOperationHandleCaptureRegistry(),
            TestContext.Current.CancellationToken).AsTask();
        var exception = await secondCall.Should().ThrowAsync<ExternalOperationRecoveryException>();

        exception.Which.State.Should().Be(ExternalOperationRecoveryState.UnknownAfterRecovery);
        exception.Which.Message.Should().NotContain("handle-opaque");
        inner.StartCount.Should().Be(1);
    }

    [Fact]
    public async Task Registered_decorator_runs_through_runtime_and_allows_provider_to_capture_initial_task()
    {
        var journal = new TestJournal();
        var verifier = new LocalSemanticFingerprintVerifier();
        var inner = new TestProvider(journal, captureOnStart: true);
        var descriptor = new ProviderDescriptor("a2a", [new CapabilityDescriptor("agent.execute", 1)]);
        var providers = new InMemoryProviderRegistry();
        providers.Register(descriptor);
        var catalog = new InMemoryExternalOperationProviderCatalog();
        catalog.Register(
            descriptor,
            new DurableExternalOperationProvider(inner, journal, verifier, now: () => At));
        var runtime = new DelegationRuntime(
            new InMemoryDelegationAcceptanceRegistry(),
            admissionVerifier: null,
            providers,
            catalog,
            fingerprintVerifier: verifier,
            now: () => At);
        var accepted = await runtime.AcceptAsync(
            new DelegationCallerScope("durable-test"),
            new DelegationRequest(
                "durable-runtime-request",
                "Implement a bounded change",
                "a2a",
                new WorkspaceReference("local", "workspace", "revision"),
                ["The change is complete"],
                [],
                new DelegationBudget(MaximumWorkerCalls: 8, MaximumRetries: 2)),
            TestContext.Current.CancellationToken);

        var queued = await runtime.GetAsync(accepted.DelegationId, TestContext.Current.CancellationToken);
        await runtime.PumpAsync(
            accepted.DelegationId,
            queued.Progress.Revision,
            TestContext.Current.CancellationToken);

        inner.StartCount.Should().Be(1);
        var record = journal.GetOnlyRecord();
        record.Intent.Request.Correlation.Task.Should().BeNull();
        record.Capture!.Handle.Correlation.Task!.Identifier.Should().Be("task-1");
        record.RecoveryState.Should().Be(ExternalOperationRecoveryState.KnownObservable);
    }

    private static DurableExternalOperationProvider CreateDecorator(TestProvider inner, TestJournal journal) =>
        new(inner, journal, new LocalSemanticFingerprintVerifier(), supportsRecoveryObservation: true, () => At);

    private static ExternalOperationStartRequest CreateRequest()
    {
        var delegation = new DelegationId(Guid.Parse("00000000-0000-0000-0000-000000000101"));
        var agent = new ExternalAgentReference("a2a", "agent-1", "a2a-0.3");
        var workflow = new WorkflowRunExecutionReference("zhinu", "run-1", "epoch-1");
        var node = new StructuralNodeReference("implement");
        var generation = new NodeGenerationId(Guid.Parse("00000000-0000-0000-0000-000000000111"));
        var correlation = new ExternalOperationCorrelation(
            delegation,
            workflow,
            node,
            generation,
            "attempt-1",
            agent);
        var semantic = new ExternalOperationSemanticInputEnvelope(
            delegation,
            agent,
            "agent.execute",
            [],
            null,
            null);
        var verifier = new LocalSemanticFingerprintVerifier();
        var identity = new ExternalOperationStartIdentity(
            delegation,
            workflow,
            node,
            generation,
            "attempt-1",
            "idempotency-1",
            verifier.Compute(semantic));
        return new ExternalOperationStartRequest(identity, correlation, "agent.execute", []);
    }

    private sealed class RecordingSink(TestJournal journal, ExternalOperationStartIdentity identity)
        : IExternalOperationHandleCaptureSink
    {
        public List<ExternalOperationHandleCapture> Captures { get; } = [];

        public async ValueTask CaptureAsync(ExternalOperationHandleCapture capture, CancellationToken cancellationToken = default)
        {
            var record = await journal.ReadAsync(identity, cancellationToken);
            record!.Capture.Should().Be(capture);
            Captures.Add(capture);
        }
    }

    private sealed class TestProvider(TestJournal journal, bool captureOnStart, bool throwAfterStart = false)
        : IExternalOperationProvider
    {
        public int StartCount { get; private set; }

        public async ValueTask<ExternalOperationStartReceipt> StartAsync(
            ExternalOperationStartRequest request,
            IExternalOperationHandleCaptureSink handleSink,
            CancellationToken cancellationToken = default)
        {
            StartCount++;
            (await journal.ReadAsync(request.Identity, cancellationToken)).Should().NotBeNull();
            var agent = request.Correlation.Agent;
            var correlation = new ExternalOperationCorrelation(
                request.Correlation.DelegationId,
                request.Correlation.WorkflowRun,
                request.Correlation.StructuralNode,
                request.Correlation.NodeGeneration,
                request.Correlation.ExecutionAttemptId,
                agent,
                new ExternalTaskReference(agent.Provider, "task-1"));
            var handle = new ExternalOperationHandle(agent.Provider, "handle-opaque", agent.ProtocolVersion, correlation);
            if (captureOnStart)
            {
                await handleSink.CaptureAsync(new ExternalOperationHandleCapture(handle, At), cancellationToken);
            }

            if (throwAfterStart)
            {
                throw new InvalidOperationException("provider start interrupted");
            }

            return new ExternalOperationStartReceipt(
                request.Identity,
                handle,
                ExternalOperationStartDisposition.Created,
                ExternalOperationState.Accepted,
                At);
        }

        public ValueTask<ExternalOperationObservation> ObserveAsync(
            ExternalOperationHandle handle,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ExternalOperationObservation(
                handle,
                1,
                ExternalOperationState.Running,
                At.AddSeconds(1)));

        public ValueTask<ExternalOperationResult> GetResultAsync(
            ExternalOperationHandle handle,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<ExternalOperationCancellationReceipt> CancelAsync(
            ExternalOperationCancelRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<ExternalOperationResumeReceipt> ResumeAsync(
            ExternalOperationResumeRequest request,
            IExternalOperationHandleCaptureSink handleSink,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class TestJournal : IExternalOperationRecoveryJournal
    {
        private readonly Dictionary<ExternalOperationAttemptKey, ExternalOperationRecoveryRecord> records = [];

        public ExternalOperationRecoveryRecord GetOnlyRecord() =>
            records.Values.Should().ContainSingle().Which;

        public ValueTask<bool> TryCreateIntentAsync(
            ExternalOperationRecoveryIntent intent,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = ExternalOperationAttemptKey.Create(intent.Request.Identity);
            if (records.ContainsKey(key))
            {
                return ValueTask.FromResult(false);
            }

            records.Add(key, new ExternalOperationRecoveryRecord(
                intent,
                recoveryState: ExternalOperationRecoveryState.StartIntent));
            return ValueTask.FromResult(true);
        }

        public ValueTask<ExternalOperationRecoveryRecord?> ReadAsync(
            ExternalOperationStartIdentity identity,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            records.TryGetValue(ExternalOperationAttemptKey.Create(identity), out var record);
            return ValueTask.FromResult(record);
        }

        public ValueTask CaptureAsync(
            ExternalOperationStartIdentity identity,
            ExternalOperationHandleCapture capture,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = ExternalOperationAttemptKey.Create(identity);
            var record = records[key];
            records[key] = new ExternalOperationRecoveryRecord(
                record.Intent,
                capture,
                record.Observation,
                record.Result,
                record.Result is null
                    ? ExternalOperationRecoveryState.KnownObservable
                    : ExternalOperationRecoveryState.Completed);
            return ValueTask.CompletedTask;
        }

        public ValueTask RecordObservationAsync(
            ExternalOperationStartIdentity identity,
            ExternalOperationObservation observation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = ExternalOperationAttemptKey.Create(identity);
            var record = records[key];
            records[key] = new ExternalOperationRecoveryRecord(
                record.Intent,
                record.Capture,
                observation,
                record.Result,
                record.Result is null
                    ? ExternalOperationRecoveryState.KnownObservable
                    : ExternalOperationRecoveryState.Completed);
            return ValueTask.CompletedTask;
        }

        public ValueTask RecordResultAsync(
            ExternalOperationStartIdentity identity,
            ExternalOperationResult result,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = ExternalOperationAttemptKey.Create(identity);
            var record = records[key];
            records[key] = new ExternalOperationRecoveryRecord(
                record.Intent,
                record.Capture,
                record.Observation,
                result,
                ExternalOperationRecoveryState.Completed);
            return ValueTask.CompletedTask;
        }

        public ValueTask MarkKnownNotReconnectableAsync(
            ExternalOperationStartIdentity identity,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = ExternalOperationAttemptKey.Create(identity);
            var record = records[key];
            records[key] = new ExternalOperationRecoveryRecord(
                record.Intent,
                record.Capture,
                record.Observation,
                record.Result,
                ExternalOperationRecoveryState.KnownNotReconnectable);
            return ValueTask.CompletedTask;
        }
    }
}
