#pragma warning disable xUnit1051

using FluentAssertions;

namespace Penghou.Qingniao.Tests;

public sealed class M27CoordinatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Validation_failure_is_corrected_once_and_fresh_pair_completes_revision_two()
    {
        var validator = new RevisionValidator();
        var reviewer = new RevisionReviewer();
        var corrector = new FixedCorrector();
        var (coordinator, _) = CreateCoordinator(validator, reviewer, corrector);
        var accepted = await coordinator.AcceptAsync(new DelegationCallerScope("caller"), Request("fix"));
        var resultPhase = await RunToResultPhaseAsync(coordinator, accepted.DelegationId);

        var terminal = await coordinator.PumpAsync(accepted.DelegationId, resultPhase.Progress.Revision);

        terminal.Progress.State.Should().Be(DelegationState.Completed, terminal.Result?.Summary + " [" + string.Join(";", terminal.Result?.UnresolvedConcerns ?? []) + "]");
        terminal.Progress.WorkerCalls.Should().Be(resultPhase.Progress.WorkerCalls + 6);
        terminal.Result!.Candidate!.Revision.Should().Be(2);
        terminal.Result.ResultReference!.Candidate.Revision.Should().Be(2);
        terminal.Result.NormalizedEvidence!.Validations.Should().HaveCount(2);
        terminal.Result.NormalizedEvidence.Reviews.Should().HaveCount(2);
        terminal.Result.NormalizedEvidence.Invocations.Should().HaveCount(2);
        corrector.Calls.Should().Be(1);
        validator.Calls.Should().Be(2);
        reviewer.Calls.Should().Be(2);
        validator.FirstCorrelation!.NodeGeneration.Should().NotBe(validator.SecondCorrelation!.NodeGeneration);
        reviewer.FirstCorrelation!.NodeGeneration.Should().NotBe(reviewer.SecondCorrelation!.NodeGeneration);
    }

    [Fact]
    public async Task Concurrent_pumps_share_one_correction_and_one_fresh_pair()
    {
        var validator = new RevisionValidator();
        var reviewer = new RevisionReviewer();
        var corrector = new BlockingCorrector();
        var (coordinator, _) = CreateCoordinator(validator, reviewer, corrector);
        var accepted = await coordinator.AcceptAsync(new DelegationCallerScope("caller"), Request("concurrent", 9));
        var resultPhase = await RunToResultPhaseAsync(coordinator, accepted.DelegationId);

        var first = coordinator.PumpAsync(accepted.DelegationId, resultPhase.Progress.Revision).AsTask();
        var second = coordinator.PumpAsync(accepted.DelegationId, resultPhase.Progress.Revision).AsTask();
        await corrector.Started.Task;
        corrector.Release();
        var results = await Task.WhenAll(first, second);

        results.Should().OnlyContain(snapshot => snapshot.Progress.State == DelegationState.Completed);
        corrector.Calls.Should().Be(1);
        validator.Calls.Should().Be(2);
        reviewer.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Invalid_corrected_identity_fails_and_keeps_generation_one_result_reference()
    {
        var (coordinator, _) = CreateCoordinator(new RevisionValidator(), new RevisionReviewer(), new InvalidCorrector());
        var accepted = await coordinator.AcceptAsync(new DelegationCallerScope("caller"), Request("invalid", 9));
        var resultPhase = await RunToResultPhaseAsync(coordinator, accepted.DelegationId);

        var terminal = await coordinator.PumpAsync(accepted.DelegationId, resultPhase.Progress.Revision);

        terminal.Progress.State.Should().Be(DelegationState.Failed);
        terminal.Result!.Candidate.Should().NotBeNull(terminal.Result.Summary);
        terminal.Result.Candidate!.Revision.Should().Be(1);
        terminal.Result.NormalizedEvidence!.Validations.Should().ContainSingle();
        terminal.Progress.WorkerCalls.Should().Be(resultPhase.Progress.WorkerCalls + 4);
    }

    [Fact]
    public async Task Correction_budget_boundary_records_actual_initial_work_without_fix()
    {
        var corrector = new FixedCorrector();
        var (coordinator, _) = CreateCoordinator(new RevisionValidator(), new RevisionReviewer(), corrector);
        var accepted = await coordinator.AcceptAsync(new DelegationCallerScope("caller"), Request("budget", 5));
        var resultPhase = await RunToResultPhaseAsync(coordinator, accepted.DelegationId);

        var terminal = await coordinator.PumpAsync(accepted.DelegationId, resultPhase.Progress.Revision);

        terminal.Progress.State.Should().Be(DelegationState.BudgetExceeded);
        terminal.Progress.WorkerCalls.Should().Be(5);
        terminal.Result!.BudgetExceeded!.Consumed.Value.Should().Be(5);
        terminal.Result.Candidate!.Revision.Should().Be(1);
        corrector.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Second_cycle_failure_is_terminal_and_never_corrected_again()
    {
        var corrector = new FixedCorrector();
        var (coordinator, _) = CreateCoordinator(new RevisionValidator(failRevisionTwo: true), new RevisionReviewer(), corrector);
        var accepted = await coordinator.AcceptAsync(new DelegationCallerScope("caller"), Request("one-fix", 9));
        var resultPhase = await RunToResultPhaseAsync(coordinator, accepted.DelegationId);

        var terminal = await coordinator.PumpAsync(accepted.DelegationId, resultPhase.Progress.Revision);

        terminal.Progress.State.Should().Be(DelegationState.Failed);
        corrector.Calls.Should().Be(1);
        terminal.Result!.NormalizedEvidence!.Validations.Should().HaveCount(2);
        terminal.Progress.WorkerCalls.Should().Be(resultPhase.Progress.WorkerCalls + 6);
    }

    [Fact]
    public async Task Initial_evaluator_fault_is_not_correction_eligible()
    {
        var throwing = new ThrowingValidator();
        var corrector = new FixedCorrector();
        var (coordinator, _) = CreateCoordinator(throwing, new RevisionReviewer(), corrector);
        var accepted = await coordinator.AcceptAsync(new DelegationCallerScope("caller"), Request("fault", 9));
        var resultPhase = await RunToResultPhaseAsync(coordinator, accepted.DelegationId);

        var terminal = await coordinator.PumpAsync(accepted.DelegationId, resultPhase.Progress.Revision);

        terminal.Progress.State.Should().Be(DelegationState.Failed);
        corrector.Calls.Should().Be(0);
        terminal.Result!.NormalizedEvidence!.Reviews.Should().ContainSingle();
    }

    [Fact]
    public async Task Single_configured_evaluator_uses_one_call_for_budget_and_terminal_accounting()
    {
        var validator = new RevisionValidator();
        var (coordinator, _) = CreateCoordinator(validator, null, null);
        var accepted = await coordinator.AcceptAsync(new DelegationCallerScope("caller"), Request("single-evaluator", 3));
        var resultPhase = await RunToResultPhaseAsync(coordinator, accepted.DelegationId);

        var terminal = await coordinator.PumpAsync(accepted.DelegationId, resultPhase.Progress.Revision);

        terminal.Progress.State.Should().Be(DelegationState.BudgetExceeded);
        terminal.Progress.WorkerCalls.Should().Be(resultPhase.Progress.WorkerCalls + 1);
        terminal.Result!.BudgetExceeded!.Consumed.Value.Should().Be(3);
        validator.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Cancellation_during_cycle_two_evaluation_publishes_cancelled_with_current_evidence()
    {
        var validator = new BlockingSecondValidator();
        var (coordinator, _) = CreateCoordinator(validator, new RevisionReviewer(), new FixedCorrector(), confirmCancellation: true);
        var accepted = await coordinator.AcceptAsync(new DelegationCallerScope("caller"), Request("cancel-cycle-two", 9));
        var resultPhase = await RunToResultPhaseAsync(coordinator, accepted.DelegationId);
        var pump = coordinator.PumpAsync(accepted.DelegationId, resultPhase.Progress.Revision).AsTask();
        await validator.Started.Task;

        await coordinator.CancelAsync(accepted.DelegationId, resultPhase.Progress.Revision, "cancel-cycle-two-1", "stop");
        validator.Release();
        var terminal = await pump;

        terminal.Progress.State.Should().Be(DelegationState.Cancelled);
        terminal.Result!.NormalizedEvidence!.Invocations.Should().HaveCount(2);
        terminal.Result.NormalizedEvidence.Validations.Should().HaveCount(1);
        terminal.Result.NormalizedEvidence.Reviews.Should().HaveCount(1);
        terminal.Result.NormalizedEvidence.Invocations.Should().Contain(invocation => invocation.NodeGeneration == terminal.Result.Candidate!.NodeGeneration);
    }

    [Fact]
    public async Task Requested_cancel_remains_nonterminal_until_provider_success_reconciliation()
    {
        var validator = new BlockingSecondValidator();
        var (coordinator, _) = CreateCoordinator(validator, new RevisionReviewer(), new FixedCorrector());
        var accepted = await coordinator.AcceptAsync(new DelegationCallerScope("caller"), Request("requested-cancel", 9));
        var resultPhase = await RunToResultPhaseAsync(coordinator, accepted.DelegationId);
        var pump = coordinator.PumpAsync(accepted.DelegationId, resultPhase.Progress.Revision).AsTask();
        await validator.Started.Task;

        var requested = await coordinator.CancelAsync(accepted.DelegationId, resultPhase.Progress.Revision, "requested-cancel-1", "stop");
        validator.Release();
        var ambiguous = await pump;
        ambiguous.Progress.State.Should().NotBe(DelegationState.Cancelled);
        requested.Progress.State.Should().NotBe(DelegationState.Cancelled);

        var observed = await coordinator.PumpAsync(accepted.DelegationId, ambiguous.Progress.Revision);
        var result = await coordinator.PumpAsync(accepted.DelegationId, observed.Progress.Revision);
        var terminal = await coordinator.PumpAsync(accepted.DelegationId, result.Progress.Revision);

        terminal.Progress.State.Should().Be(DelegationState.Completed);
    }

    [Fact]
    public async Task Confirmed_cancel_during_cycle_two_keeps_generation_two_evidence()
    {
        var validator = new BlockingSecondValidator();
        var (coordinator, _) = CreateCoordinator(validator, new RevisionReviewer(), new FixedCorrector(), confirmCancellation: true);
        var accepted = await coordinator.AcceptAsync(new DelegationCallerScope("caller"), Request("confirmed-cancel", 9));
        var resultPhase = await RunToResultPhaseAsync(coordinator, accepted.DelegationId);
        var pump = coordinator.PumpAsync(accepted.DelegationId, resultPhase.Progress.Revision).AsTask();
        await validator.Started.Task;

        var cancelled = await coordinator.CancelAsync(accepted.DelegationId, resultPhase.Progress.Revision, "confirmed-cancel-1", "stop");
        validator.Release();
        var terminal = await pump;

        cancelled.Progress.State.Should().Be(DelegationState.Cancelled);
        terminal.Progress.State.Should().Be(DelegationState.Cancelled);
        terminal.Result!.NormalizedEvidence!.Invocations.Should().Contain(invocation => invocation.NodeGeneration == terminal.Result.Candidate!.NodeGeneration);
    }

    [Fact]
    public async Task Durable_cancel_during_correction_prevents_late_completion()
    {
        var corrector = new BlockingCorrector();
        var (coordinator, _) = CreateCoordinator(new RevisionValidator(), new RevisionReviewer(), corrector);
        var accepted = await coordinator.AcceptAsync(new DelegationCallerScope("caller"), Request("cancel", 9));
        var resultPhase = await RunToResultPhaseAsync(coordinator, accepted.DelegationId);
        var pump = coordinator.PumpAsync(accepted.DelegationId, resultPhase.Progress.Revision).AsTask();
        await corrector.Started.Task;

        var cancelled = await coordinator.CancelAsync(accepted.DelegationId, resultPhase.Progress.Revision, "cancel-1", "stop");
        corrector.Release();
        var after = await pump;

        after.Progress.State.Should().NotBe(DelegationState.Completed);
        cancelled.Progress.State.Should().NotBe(DelegationState.Completed);
        (await coordinator.GetAsync(accepted.DelegationId)).Progress.State.Should().NotBe(DelegationState.Completed);
    }

    private static async Task<DelegationExecutionSnapshot> RunToResultPhaseAsync(InMemoryDelegationCoordinator coordinator, DelegationId id)
    {
        var queued = await coordinator.GetAsync(id);
        var running = await coordinator.PumpAsync(id, queued.Progress.Revision);
        return await coordinator.PumpAsync(id, running.Progress.Revision);
    }

    private static (InMemoryDelegationCoordinator Coordinator, CandidateProvider Provider) CreateCoordinator(
        IDeterministicCandidateValidator? validator,
        IIndependentCandidateReviewer? reviewer,
        ICandidateCorrector? corrector,
        bool confirmCancellation = false)
    {
        var descriptor = new ProviderDescriptor("m27-provider", [new CapabilityDescriptor("agent.execute", 1)]);
        var providers = new InMemoryProviderRegistry();
        providers.Register(descriptor);
        var provider = new CandidateProvider { ConfirmCancellation = confirmCancellation };
        var adapters = new InMemoryExternalOperationProviderCatalog();
        adapters.Register(descriptor, provider);
        return (new InMemoryDelegationCoordinator(
            new InMemoryDelegationAcceptanceRegistry(),
            new InMemoryWorkflowPlanResolver(),
            providers,
            adapters,
            new SimingExternalOperationSemanticFingerprintVerifier(),
            now: () => Start,
            candidateValidator: validator,
            candidateReviewer: reviewer,
            candidateCorrector: corrector), provider);
    }

    private static DelegationRequest Request(string key, int maximumWorkerCalls = 8) => new(
        key,
        "Implement the objective",
        new WorkspaceReference("local", "workspace", "revision"),
        ["The result is correct"],
        [],
        new DelegationBudget(maximumWorkerCalls, 2));

    private sealed class CandidateProvider : IExternalOperationProvider
    {
        private ExternalOperationHandle? handle;
        public bool ConfirmCancellation { get; init; }

        public async ValueTask<ExternalOperationStartReceipt> StartAsync(ExternalOperationStartRequest request, IExternalOperationHandleCaptureSink sink, CancellationToken cancellationToken = default)
        {
            var correlation = new ExternalOperationCorrelation(request.Correlation.DelegationId, request.Correlation.WorkflowRun, request.Correlation.StructuralNode, request.Correlation.NodeGeneration, request.Correlation.ExecutionAttemptId, request.Correlation.Agent, new ExternalTaskReference(request.Correlation.Agent.Provider, "m27-handle"));
            handle = new ExternalOperationHandle(request.Correlation.Agent.Provider, "m27-handle", request.Correlation.Agent.ProtocolVersion, correlation);
            await sink.CaptureAsync(new ExternalOperationHandleCapture(handle, Start.AddMinutes(1)), cancellationToken);
            return new ExternalOperationStartReceipt(request.Identity, handle, ExternalOperationStartDisposition.Created, ExternalOperationState.Running, Start.AddMinutes(1));
        }

        public ValueTask<ExternalOperationObservation> ObserveAsync(ExternalOperationHandle operationHandle, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ExternalOperationObservation(operationHandle, 1, ExternalOperationState.Succeeded, Start.AddMinutes(2), resultAvailable: true));

        public ValueTask<ExternalOperationResult> GetResultAsync(ExternalOperationHandle operationHandle, CancellationToken cancellationToken = default)
        {
            var artifact = Artifact(operationHandle.Correlation, "candidate-v1", 'a');
            var candidate = new CandidateRevisionReference(operationHandle.Correlation.DelegationId, operationHandle.Correlation.StructuralNode, operationHandle.Correlation.NodeGeneration, new CandidateId(operationHandle.Correlation.DelegationId.Value), 1, artifact.ContentIdentity, [artifact]);
            return ValueTask.FromResult(new ExternalOperationResult(operationHandle, ExternalOperationState.Succeeded, Start.AddMinutes(3), "done", [artifact], candidate: candidate));
        }

        public ValueTask<ExternalOperationCancellationReceipt> CancelAsync(ExternalOperationCancelRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ExternalOperationCancellationReceipt(
                request.Handle,
                request.CancellationKey,
                ConfirmCancellation
                    ? ExternalOperationCancellationDisposition.ConfirmedCancelled
                    : ExternalOperationCancellationDisposition.Requested,
                ConfirmCancellation ? ExternalOperationState.Cancelled : ExternalOperationState.CancellationRequested,
                Start.AddMinutes(4)));

        public ValueTask<ExternalOperationResumeReceipt> ResumeAsync(ExternalOperationResumeRequest request, IExternalOperationHandleCaptureSink sink, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RevisionValidator(bool failRevisionTwo = false) : EvaluatorBase, IDeterministicCandidateValidator
    {
        public int Calls { get; private set; }
        public ExternalOperationCorrelation? FirstCorrelation { get; private set; }
        public ExternalOperationCorrelation? SecondCorrelation { get; private set; }
        public ValueTask<ValidationEvidence> ValidateAsync(CandidateValidationRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (request.Candidate.Revision == 1) FirstCorrelation = request.Correlation; else SecondCorrelation = request.Correlation;
            var outcome = request.Candidate.Revision == 1 || failRevisionTwo ? "failed" : "passed";
            return ValueTask.FromResult(new ValidationEvidence(Invocation(request, "deterministic.validation", "validator", request.InvocationId), outcome, []));
        }
    }

    private sealed class RevisionReviewer : EvaluatorBase, IIndependentCandidateReviewer
    {
        public int Calls { get; private set; }
        public ExternalOperationCorrelation? FirstCorrelation { get; private set; }
        public ExternalOperationCorrelation? SecondCorrelation { get; private set; }
        public ValueTask<ReviewEvidence> ReviewAsync(CandidateReviewRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (request.Candidate.Revision == 1) FirstCorrelation = request.Correlation; else SecondCorrelation = request.Correlation;
            var invocation = new WorkerInvocationEvidence(request.Candidate.DelegationId, request.Candidate.StructuralNode, request.Candidate.NodeGeneration, EvidenceKinds.ModelExecution, new ProviderExecutionAttemptReference("reviewer", request.InvocationId, "evaluator"), "succeeded", Start.AddMinutes(4), Start.AddMinutes(5), EvidenceKinds.ModelExecution, "review", "reviewer", "review-model", "review-model", [], request.Candidate.Artifacts, request.Candidate.Artifacts, request.Candidate);
            var independence = new ReviewIndependenceEvidence(request.ImplementationInvocation.Attempt.AttemptId, request.InvocationId, IndependenceAssessment.Unknown, IndependenceAssessment.Different, IndependenceAssessment.Different, IndependenceAssessment.Different, IndependenceAssessment.Different);
            return ValueTask.FromResult(new ReviewEvidence(invocation, request.Candidate.Revision == 1 ? "rejected" : "approved", [], independence, request.Candidate, "reviewer"));
        }
    }

    private sealed class ThrowingValidator : IDeterministicCandidateValidator
    {
        public ValueTask<ValidationEvidence> ValidateAsync(CandidateValidationRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("deterministic fault");
    }

    private sealed class BlockingSecondValidator : EvaluatorBase, IDeterministicCandidateValidator
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<ValidationEvidence> ValidateAsync(CandidateValidationRequest request, CancellationToken cancellationToken = default) =>
            request.Candidate.Revision == 1
                ? ValueTask.FromResult(new ValidationEvidence(Invocation(request, "deterministic.validation", "validator", request.InvocationId), "failed", []))
                : AwaitSecondAsync(request);

        public void Release() => release.TrySetResult();

        private async ValueTask<ValidationEvidence> AwaitSecondAsync(CandidateValidationRequest request)
        {
            Started.TrySetResult();
            await release.Task.ConfigureAwait(false);
            return new ValidationEvidence(Invocation(request, "deterministic.validation", "validator", request.InvocationId), "passed", []);
        }
    }

    private sealed class FixedCorrector : ICandidateCorrector
    {
        public int Calls { get; private set; }
        public ValueTask<CandidateCorrectionOutcome> CorrectAsync(CandidateCorrectionRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(CreateOutcome(request));
        }
    }

    private sealed class BlockingCorrector : ICandidateCorrector
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public async ValueTask<CandidateCorrectionOutcome> CorrectAsync(CandidateCorrectionRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            Started.TrySetResult();
            await release.Task.ConfigureAwait(false);
            return CreateOutcome(request);
        }
        public void Release() => release.TrySetResult();
    }

    private sealed class InvalidCorrector : ICandidateCorrector
    {
        public ValueTask<CandidateCorrectionOutcome> CorrectAsync(CandidateCorrectionRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new CandidateCorrectionOutcome(request.SourceCandidate, new WorkerInvocationEvidence(request.SourceCandidate.DelegationId, request.SourceCandidate.StructuralNode, request.SourceCandidate.NodeGeneration, EvidenceKinds.AgentExecution, new ProviderExecutionAttemptReference("m27-provider", "bad", "bad"), "succeeded", Start, Start.AddMinutes(1), "agent.execute", "implement", "m27-provider", null, "model", [], request.SourceCandidate.Artifacts, request.SourceCandidate.Artifacts, request.SourceCandidate)));
    }

    private abstract class EvaluatorBase
    {
        protected static WorkerInvocationEvidence Invocation(CandidateValidationRequest request, string category, string provider, string attemptId) =>
            new(request.Candidate.DelegationId, request.Candidate.StructuralNode, request.Candidate.NodeGeneration, category, new ProviderExecutionAttemptReference(provider, attemptId, "evaluator"), "succeeded", Start.AddMinutes(4), Start.AddMinutes(5), category, "profile", provider, "model", "model", [], request.Candidate.Artifacts, request.Candidate.Artifacts, request.Candidate);

        protected static WorkerInvocationEvidence Invocation(WorkerInvocationEvidence implementation, CandidateRevisionReference candidate, string category, string provider, string attemptId) =>
            new(candidate.DelegationId, candidate.StructuralNode, candidate.NodeGeneration, category, new ProviderExecutionAttemptReference(provider, attemptId, "evaluator"), "succeeded", Start.AddMinutes(4), Start.AddMinutes(5), category, "profile", provider, "model", "model", [], candidate.Artifacts, candidate.Artifacts, candidate);
    }

    private static CandidateCorrectionOutcome CreateOutcome(CandidateCorrectionRequest request)
    {
        var artifact = Artifact(request.Correlation, "candidate-v2", 'b');
        var candidate = new CandidateRevisionReference(request.SourceCandidate.DelegationId, request.SourceCandidate.StructuralNode, request.TargetGeneration, request.SourceCandidate.CandidateId, request.TargetRevision, artifact.ContentIdentity, [artifact]);
        var invocation = new WorkerInvocationEvidence(candidate.DelegationId, candidate.StructuralNode, candidate.NodeGeneration, EvidenceKinds.AgentExecution, new ProviderExecutionAttemptReference("m27-provider", request.Correlation.ExecutionAttemptId, "correction-handle"), "succeeded", Start.AddMinutes(6), Start.AddMinutes(7), "agent.execute", "implement", "m27-provider", null, "model", [], candidate.Artifacts, candidate.Artifacts, candidate, executionCorrelation: request.Correlation);
        return new CandidateCorrectionOutcome(candidate, invocation);
    }

    private static DelegationArtifactReference Artifact(ExternalOperationCorrelation correlation, string id, char hash) =>
        new(correlation.DelegationId, correlation.StructuralNode, correlation.NodeGeneration, "m27-provider", "repo", id, "application/octet-stream", 1, id, ArtifactContentIdentity.Sha256Bytes(new string(hash, 64)));
}
