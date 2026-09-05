using FluentAssertions;

namespace Penghou.Qingniao.Tests;

public sealed class ExternalOperationContractsTests
{
    [Fact]
    public void Start_identity_and_correlation_are_stable_and_handle_requires_task_capture()
    {
        var identity = CreateIdentity();
        var correlationWithoutTask = CreateCorrelation();
        var correlation = CreateCorrelation(new ExternalTaskReference("a2a", "task-1"));

        var missingTask = () => new ExternalOperationHandle("a2a", "handle-1", "a2a-0.3", correlationWithoutTask);
        missingTask.Should().Throw<InvalidOperationException>();

        var handle = CreateHandle(correlation);
        var request = new ExternalOperationStartRequest(identity, correlation, "implement-code", []);
        var receipt = new ExternalOperationStartReceipt(
            identity,
            handle,
            ExternalOperationStartDisposition.Created,
            ExternalOperationState.Accepted,
            At(1));

        request.Correlation.Should().Be(correlation);
        receipt.Handle.Correlation.Task!.Identifier.Should().Be("task-1");
        receipt.Handle.ToProviderAttemptReference().AttemptId.Should().Be("attempt-1");

        var mismatch = () => new ExternalOperationStartRequest(
            identity,
            CreateCorrelation(new ExternalTaskReference("a2a", "task-2"), "attempt-2"),
            "implement-code",
            []);
        mismatch.Should().Throw<ArgumentException>();

        var wrongProvider = () => new ExternalOperationHandle(
            "process",
            "handle-1",
            "a2a-0.3",
            correlation);
        var wrongProtocol = () => new ExternalOperationHandle(
            "a2a",
            "handle-1",
            "a2a-0.2",
            correlation);
        var wrongTaskProvider = () => CreateCorrelation(new ExternalTaskReference("process", "task-2"));
        wrongProvider.Should().Throw<ArgumentException>();
        wrongProtocol.Should().Throw<ArgumentException>();
        wrongTaskProvider.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Handle_capture_sink_supports_early_capture_before_start_receipt()
    {
        var sink = new RecordingHandleSink();
        var capture = new ExternalOperationHandleCapture(CreateHandle(), At(2));

        await sink.CaptureAsync(capture, TestContext.Current.CancellationToken);

        sink.Captures.Should().ContainSingle().Which.Should().Be(capture);
    }

    [Fact]
    public void Observation_revisions_are_monotonic_and_terminal_state_is_immutable()
    {
        var handle = CreateHandle();
        var first = new ExternalOperationObservation(handle, 1, ExternalOperationState.Running, At(1));
        var second = new ExternalOperationObservation(handle, 2, ExternalOperationState.Waiting, At(2), "awaiting-input");
        var terminal = new ExternalOperationObservation(
            handle,
            3,
            ExternalOperationState.Failed,
            At(3),
            failure: new ExternalOperationFailure(
                ExternalOperationFailureKind.Remote,
                "remote.failed",
                "The remote task failed.",
                retryable: true),
            resultAvailable: true);

        var changedRevision = () => ExternalOperationObservationRules.ValidateProgression(
            first,
            new ExternalOperationObservation(handle, 1, ExternalOperationState.Waiting, At(1)));
        var backwards = () => ExternalOperationObservationRules.ValidateProgression(
            second,
            new ExternalOperationObservation(handle, 1, ExternalOperationState.Waiting, At(2)));
        var changedTerminal = () => ExternalOperationObservationRules.ValidateProgression(
            terminal,
            new ExternalOperationObservation(handle, 4, ExternalOperationState.Succeeded, At(4), resultAvailable: true));

        ExternalOperationObservationRules.ValidateProgression(first, second);
        ExternalOperationObservationRules.ValidateProgression(second, terminal);
        changedRevision.Should().Throw<InvalidOperationException>();
        backwards.Should().Throw<InvalidOperationException>();
        changedTerminal.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Failure_taxonomy_is_precise_for_terminal_results_and_observations()
    {
        var handle = CreateHandle();
        var transport = new ExternalOperationFailure(ExternalOperationFailureKind.Transport, "transport.unavailable", "Connection lost.", true);
        var remote = new ExternalOperationFailure(ExternalOperationFailureKind.Remote, "remote.failed", "Remote execution failed.", true);
        var cancellation = new ExternalOperationFailure(ExternalOperationFailureKind.Cancellation, "cancel.rejected", "The provider did not confirm cancellation.", false);
        var timeout = new ExternalOperationFailure(ExternalOperationFailureKind.Timeout, "operation.timeout", "The operation exceeded its deadline.", true);
        var rejection = new ExternalOperationFailure(ExternalOperationFailureKind.Rejection, "remote.rejected", "The provider rejected the task.", false);
        var validation = new ExternalOperationFailure(ExternalOperationFailureKind.ResultValidation, "result.invalid", "The provider result failed validation.", false);

        new ExternalOperationObservation(handle, 1, ExternalOperationState.Unknown, At(1), failure: transport).Failure!.Kind.Should().Be(ExternalOperationFailureKind.Transport);
        new ExternalOperationObservation(handle, 2, ExternalOperationState.Failed, At(2), failure: remote).Failure!.Kind.Should().Be(ExternalOperationFailureKind.Remote);
        new ExternalOperationObservation(handle, 3, ExternalOperationState.Cancelled, At(3)).Failure.Should().BeNull();
        new ExternalOperationObservation(handle, 4, ExternalOperationState.TimedOut, At(4), failure: timeout).Failure!.Kind.Should().Be(ExternalOperationFailureKind.Timeout);
        new ExternalOperationObservation(handle, 5, ExternalOperationState.Rejected, At(5), failure: rejection).Failure!.Kind.Should().Be(ExternalOperationFailureKind.Rejection);
        new ExternalOperationObservation(handle, 6, ExternalOperationState.Failed, At(6), failure: validation).Failure!.Kind.Should().Be(ExternalOperationFailureKind.ResultValidation);

        var wrong = () => new ExternalOperationResult(
            handle,
            ExternalOperationState.TimedOut,
            At(7),
            "timed out",
            [],
            failure: remote);
        wrong.Should().Throw<ArgumentException>();

        var missingFailure = () => new ExternalOperationResult(
            handle,
            ExternalOperationState.Rejected,
            At(8),
            "rejected",
            []);
        missingFailure.Should().Throw<ArgumentException>();

        var cancelledObservationWithFailure = () => new ExternalOperationObservation(
            handle,
            9,
            ExternalOperationState.Cancelled,
            At(9),
            failure: cancellation);
        var cancelledResultWithFailure = () => new ExternalOperationResult(
            handle,
            ExternalOperationState.Cancelled,
            At(10),
            "cancelled",
            [],
            failure: cancellation);
        cancelledObservationWithFailure.Should().Throw<ArgumentException>();
        cancelledResultWithFailure.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Cancellation_and_resume_are_idempotency_addressed_and_do_not_carry_transcripts()
    {
        var handle = CreateHandle();
        var cancel = new ExternalOperationCancelRequest(handle, "cancel-key-1", "Supervisor requested cancellation.");
        var receipt = new ExternalOperationCancellationReceipt(
            handle,
            "cancel-key-1",
            ExternalOperationCancellationDisposition.ConfirmedCancelled,
            ExternalOperationState.Cancelled,
            At(4));
        var resume = new ExternalOperationResumeRequest(handle, "resume-key-1", [], "Continue after reconnect.");

        cancel.CancellationKey.Should().Be("cancel-key-1");
        receipt.CancellationKey.Should().Be("cancel-key-1");
        receipt.Disposition.Should().Be(ExternalOperationCancellationDisposition.ConfirmedCancelled);
        resume.ResumeKey.Should().Be("resume-key-1");
        resume.CorrectionArtifacts.Should().BeEmpty();

        var transcript = new Dictionary<string, string> { ["provider.transcript"] = "must not persist" };
        var unsafeUsage = () => new UsageProvenanceSnapshot(transcript);
        unsafeUsage.Should().Throw<ArgumentException>();

        new UsageProvenanceSnapshot(new Dictionary<string, string>
        {
            ["prompt-template-version"] = "v3",
        }).Measurements["prompt-template-version"].Should().Be("v3");
        var accessToken = () => new UsageProvenanceSnapshot(new Dictionary<string, string>
        {
            ["provider.access_token"] = "opaque",
        });
        accessToken.Should().Throw<ArgumentException>();

        var artifact = CreateArtifact();
        var duplicateCorrections = () => new ExternalOperationResumeRequest(
            handle,
            "resume-key-2",
            [artifact, artifact]);
        duplicateCorrections.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Provenance_snapshots_are_versioned_bounded_and_snapshotted()
    {
        var tools = new List<ToolProvenance> { new("read-files", "2", ArtifactContentIdentity.Sha256Bytes(Hash)) };
        var usage = new Dictionary<string, string> { ["input_tokens"] = "12" };
        var snapshot = new ExternalOperationProvenanceSnapshot(
            new ModelProvenanceSnapshot("baize", "review-v3", "2026-01", "review"),
            new ToolProvenanceSnapshot(tools),
            new UsageProvenanceSnapshot(usage));

        tools.Clear();
        usage["input_tokens"] = "99";

        snapshot.SchemaVersion.Should().Be("v1");
        snapshot.Model!.Model.Should().Be("review-v3");
        snapshot.Tools.Tools.Should().ContainSingle();
        snapshot.Usage.Measurements["input_tokens"].Should().Be("12");

        var duplicate = () => new ToolProvenanceSnapshot([new ToolProvenance("read-files"), new ToolProvenance("read-files")]);
        var badContentIdentity = () => new ToolProvenance(
            "read-files",
            contentIdentity: new ArtifactContentIdentity(ArtifactContentIdentity.Sha256BytesV1, "not-a-hash"));
        duplicate.Should().Throw<ArgumentException>();
        badContentIdentity.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Ambiguous_transport_is_nonterminal_and_state_progression_rejects_regressions()
    {
        var handle = CreateHandle();
        var directAccepted = new ExternalOperationObservation(handle, 0, ExternalOperationState.Accepted, At(0));
        var directSucceeded = new ExternalOperationObservation(handle, 1, ExternalOperationState.Succeeded, At(1), resultAvailable: true);
        ExternalOperationObservationRules.ValidateProgression(directAccepted, directSucceeded);
        var running = new ExternalOperationObservation(handle, 1, ExternalOperationState.Running, At(1));
        var unknown = new ExternalOperationObservation(
            handle,
            2,
            ExternalOperationState.Unknown,
            At(2),
            failure: new ExternalOperationFailure(ExternalOperationFailureKind.Transport, "transport.lost", "Connection lost.", true));
        var reconnected = new ExternalOperationObservation(handle, 3, ExternalOperationState.Waiting, At(3));
        var accepted = new ExternalOperationObservation(handle, 4, ExternalOperationState.Accepted, At(4));

        ExternalOperationObservationRules.ValidateProgression(running, unknown);
        ExternalOperationObservationRules.ValidateProgression(unknown, reconnected);
        var regression = () => ExternalOperationObservationRules.ValidateProgression(running, accepted);
        regression.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Cancellation_receipts_enforce_disposition_state_matrix()
    {
        var handle = CreateHandle();
        var cancellationFailure = new ExternalOperationFailure(
            ExternalOperationFailureKind.Cancellation,
            "cancel.rejected",
            "The provider rejected cancellation.",
            false);

        new ExternalOperationCancellationReceipt(handle, "cancel-1", ExternalOperationCancellationDisposition.Requested, ExternalOperationState.CancellationRequested, At(1));
        new ExternalOperationCancellationReceipt(handle, "cancel-2", ExternalOperationCancellationDisposition.ConfirmedCancelled, ExternalOperationState.Cancelled, At(2));
        new ExternalOperationCancellationReceipt(handle, "cancel-3", ExternalOperationCancellationDisposition.Unknown, ExternalOperationState.Unknown, At(3), cancellationFailure);
        new ExternalOperationCancellationReceipt(handle, "cancel-4", ExternalOperationCancellationDisposition.Rejected, ExternalOperationState.Running, At(4), cancellationFailure);
        new ExternalOperationCancellationReceipt(handle, "cancel-5", ExternalOperationCancellationDisposition.AlreadyTerminal, ExternalOperationState.Succeeded, At(5));

        var requestedWrongState = () => new ExternalOperationCancellationReceipt(handle, "cancel-6", ExternalOperationCancellationDisposition.Requested, ExternalOperationState.Running, At(6));
        var rejectedWithoutFailure = () => new ExternalOperationCancellationReceipt(handle, "cancel-7", ExternalOperationCancellationDisposition.Rejected, ExternalOperationState.Running, At(7));
        var terminalAsRejected = () => new ExternalOperationCancellationReceipt(handle, "cancel-8", ExternalOperationCancellationDisposition.Rejected, ExternalOperationState.Cancelled, At(8), cancellationFailure);
        var unknownWithoutFailure = () => new ExternalOperationCancellationReceipt(handle, "cancel-9", ExternalOperationCancellationDisposition.Unknown, ExternalOperationState.Unknown, At(9));
        requestedWrongState.Should().Throw<ArgumentException>();
        rejectedWithoutFailure.Should().Throw<ArgumentException>();
        terminalAsRejected.Should().Throw<ArgumentException>();
        unknownWithoutFailure.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Unknown_cannot_regress_to_accepted_and_external_results_own_artifacts()
    {
        var handle = CreateHandle();
        var unknown = new ExternalOperationObservation(
            handle,
            1,
            ExternalOperationState.Unknown,
            At(1),
            failure: new ExternalOperationFailure(ExternalOperationFailureKind.Transport, "transport.lost", "Connection lost.", true));
        var accepted = new ExternalOperationObservation(handle, 2, ExternalOperationState.Accepted, At(2));

        var regression = () => ExternalOperationObservationRules.ValidateProgression(unknown, accepted);
        regression.Should().Throw<InvalidOperationException>();

        var wrongNode = new DelegationArtifactReference(
            DelegationIdValue,
            new StructuralNodeReference("other-node"),
            Generation,
            "provider",
            "repository",
            "artifact-2",
            "result",
            1,
            "artifact-location",
            ArtifactContentIdentity.Sha256Bytes(Hash));
        var result = () => new ExternalOperationResult(handle, ExternalOperationState.Succeeded, At(3), "done", [wrongNode]);
        result.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Resume_receipt_retains_key_and_exact_correlation()
    {
        var handle = CreateHandle();
        var receipt = new ExternalOperationResumeReceipt(
            handle,
            "resume-key-1",
            handle,
            ExternalOperationStartDisposition.Existing,
            ExternalOperationState.Running,
            At(5));

        receipt.ResumeKey.Should().Be("resume-key-1");
        receipt.PreviousHandle.Correlation.Should().Be(receipt.Handle.Correlation);

        var other = CreateHandle(CreateCorrelation(new ExternalTaskReference("a2a", "task-1"), "attempt-2"));
        var mismatch = () => new ExternalOperationResumeReceipt(
            handle,
            "resume-key-2",
            other,
            ExternalOperationStartDisposition.Created,
            ExternalOperationState.Running,
            At(6));
        mismatch.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Semantic_fingerprint_verifier_seam_rejects_changed_input_envelopes()
    {
        var request = new ExternalOperationStartRequest(
            CreateIdentity(),
            CreateCorrelation(),
            "implement-code",
            [CreateArtifact()],
            new ExternalOperationBudgetHint(maximumTokens: 20),
            At(20));
        var verifier = new InMemorySemanticFingerprintVerifier();
        verifier.Register(request.Identity, request.SemanticInput);

        verifier.Matches(request.Identity, request.SemanticInput).Should().BeTrue();
        request.VerifySemanticFingerprint(verifier);

        var changedEnvelopes = new[]
        {
            new ExternalOperationSemanticInputEnvelope(
                request.SemanticInput.DelegationId,
                request.SemanticInput.Agent,
                "review-code",
                request.InputArtifacts,
                request.Budget,
                request.Deadline),
            new ExternalOperationSemanticInputEnvelope(
                request.SemanticInput.DelegationId,
                new ExternalAgentReference("process", "agent-2", "process-v1"),
                request.Capability,
                request.InputArtifacts,
                request.Budget,
                request.Deadline),
            new ExternalOperationSemanticInputEnvelope(
                request.SemanticInput.DelegationId,
                request.SemanticInput.Agent,
                request.Capability,
                [CreateArtifact("artifact-2")],
                request.Budget,
                request.Deadline),
            new ExternalOperationSemanticInputEnvelope(
                request.SemanticInput.DelegationId,
                request.SemanticInput.Agent,
                request.Capability,
                request.InputArtifacts,
                new ExternalOperationBudgetHint(maximumTokens: 21),
                request.Deadline),
            new ExternalOperationSemanticInputEnvelope(
                request.SemanticInput.DelegationId,
                request.SemanticInput.Agent,
                request.Capability,
                request.InputArtifacts,
                request.Budget,
                At(21)),
        };

        foreach (var changed in changedEnvelopes)
        {
            verifier.Matches(request.Identity, changed).Should().BeFalse();
        }

        var rejectingVerifier = new InMemorySemanticFingerprintVerifier();
        rejectingVerifier.Register(request.Identity, changedEnvelopes[0]);
        var rejected = () => request.VerifySemanticFingerprint(rejectingVerifier);
        rejected.Should().Throw<InvalidOperationException>();

        var duplicate = () => new ExternalOperationSemanticInputEnvelope(
            request.SemanticInput.DelegationId,
            request.SemanticInput.Agent,
            request.Capability,
            [CreateArtifact(), CreateArtifact()],
            request.Budget,
            request.Deadline);
        var wrongDelegation = new DelegationArtifactReference(
            OtherDelegationId,
            Node,
            Generation,
            "provider",
            "repository",
            "foreign-artifact",
            "input",
            1,
            "foreign-location",
            ArtifactContentIdentity.Sha256Bytes(Hash));
        var ownership = () => new ExternalOperationSemanticInputEnvelope(
            request.SemanticInput.DelegationId,
            request.SemanticInput.Agent,
            request.Capability,
            [wrongDelegation],
            request.Budget,
            request.Deadline);
        duplicate.Should().Throw<ArgumentException>();
        ownership.Should().Throw<ArgumentException>();
    }

    private static ExternalOperationStartIdentity CreateIdentity() => new(
        DelegationIdValue,
        Workflow,
        Node,
        Generation,
        "attempt-1",
        "start-key-1",
        Hash);

    private static ExternalOperationCorrelation CreateCorrelation(ExternalTaskReference? task = null, string attemptId = "attempt-1") => new(
        DelegationIdValue,
        Workflow,
        Node,
        Generation,
        attemptId,
        new ExternalAgentReference("a2a", "agent-1", "a2a-0.3"),
        task);

    private static ExternalOperationHandle CreateHandle(ExternalOperationCorrelation? correlation = null) => new(
        "a2a",
        "handle-1",
        "a2a-0.3",
        correlation ?? CreateCorrelation(new ExternalTaskReference("a2a", "task-1")));

    private static DateTimeOffset At(int seconds) => DateTimeOffset.Parse($"2026-01-01T00:00:{seconds:00}Z");

    private static DelegationArtifactReference CreateArtifact(
        string artifactId = "artifact-1",
        DelegationId? delegationId = null) => new(
        delegationId ?? DelegationIdValue,
        Node,
        Generation,
        "provider",
        "repository",
        artifactId,
        "evidence",
        1,
        $"artifact-location/{artifactId}",
        ArtifactContentIdentity.Sha256Bytes(Hash));

    private sealed class RecordingHandleSink : IExternalOperationHandleCaptureSink
    {
        public List<ExternalOperationHandleCapture> Captures { get; } = [];

        public ValueTask CaptureAsync(ExternalOperationHandleCapture capture, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Captures.Add(capture);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class InMemorySemanticFingerprintVerifier : IExternalOperationSemanticFingerprintVerifier
    {
        private readonly Dictionary<string, ExternalOperationSemanticInputEnvelope> envelopes = new(StringComparer.Ordinal);

        public void Register(ExternalOperationStartIdentity identity, ExternalOperationSemanticInputEnvelope envelope) =>
            envelopes[identity.SemanticFingerprint] = envelope;

        public bool Matches(ExternalOperationStartIdentity identity, ExternalOperationSemanticInputEnvelope semanticInput) =>
            envelopes.TryGetValue(identity.SemanticFingerprint, out var expected)
            && Equals(expected, semanticInput);
    }

    private static readonly DelegationId DelegationIdValue = new(Guid.Parse("00000000-0000-0000-0000-000000000001"));
    private static readonly DelegationId OtherDelegationId = new(Guid.Parse("00000000-0000-0000-0000-000000000002"));
    private static readonly WorkflowRunExecutionReference Workflow = new("zhinu", "run-1", "epoch-1");
    private static readonly StructuralNodeReference Node = new("implement");
    private static readonly NodeGenerationId Generation = new(Guid.Parse("00000000-0000-0000-0000-000000000011"));
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
}
