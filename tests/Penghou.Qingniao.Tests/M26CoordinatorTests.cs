#pragma warning disable xUnit1051

using FluentAssertions;

namespace Penghou.Qingniao.Tests;

public sealed class M26CoordinatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Candidate_is_published_before_overlapping_evaluators_and_subject_is_exact()
    {
        var validator = new BlockingValidator();
        var reviewer = new BlockingReviewer();
        var registry = new RecordingCandidateRegistry();
        var (coordinator, provider) = CreateCoordinator(validator, reviewer, registry);
        var accepted = await coordinator.AcceptAsync(new DelegationCallerScope("caller"), Request("order"));
        var observed = await RunToResultPhaseAsync(coordinator, accepted.DelegationId);

        var pump = coordinator.PumpAsync(accepted.DelegationId, observed.Progress.Revision).AsTask();
        await Task.WhenAll(validator.Started.Task, reviewer.Started.Task);
        registry.Published.Should().BeTrue();
        validator.Request!.Candidate.Should().BeSameAs(reviewer.Request!.Candidate);
        CandidateRevisionIdentity.SubjectEqual(validator.Request.Candidate, reviewer.Request.Candidate).Should().BeTrue();
        provider.ResultCalls.Should().Be(1);

        validator.Release();
        reviewer.Release();
        var terminal = await pump;
        terminal.Progress.State.Should().Be(DelegationState.Completed);
        terminal.Result!.ResultReference.Should().NotBeNull();
        terminal.Result.NormalizedEvidence!.Validations.Should().ContainSingle();
        terminal.Result.NormalizedEvidence.Reviews.Should().ContainSingle();
    }

    [Fact]
    public async Task Validation_failure_cannot_complete_even_when_review_approves()
    {
        var validator = new FixedValidator("failed");
        var reviewer = new FixedReviewer("approved");
        var (coordinator, _) = CreateCoordinator(validator, reviewer);
        var accepted = await coordinator.AcceptAsync(new DelegationCallerScope("caller"), Request("validation-fail"));
        var observed = await RunToResultPhaseAsync(coordinator, accepted.DelegationId);
        var terminal = await coordinator.PumpAsync(accepted.DelegationId, observed.Progress.Revision);

        terminal.Progress.State.Should().Be(DelegationState.Failed);
        terminal.Result!.Evidence.ReviewApproved.Should().BeTrue();
        terminal.Result.NormalizedEvidence!.Validations.Should().ContainSingle();
        terminal.Result.NormalizedEvidence.Reviews.Should().ContainSingle();
    }

    [Fact]
    public async Task Review_rejection_is_advisory_and_preserves_validation_evidence()
    {
        var validator = new FixedValidator("passed");
        var reviewer = new FixedReviewer("rejected");
        var (coordinator, _) = CreateCoordinator(validator, reviewer);
        var accepted = await coordinator.AcceptAsync(new DelegationCallerScope("caller"), Request("review-reject"));
        var observed = await RunToResultPhaseAsync(coordinator, accepted.DelegationId);
        var terminal = await coordinator.PumpAsync(accepted.DelegationId, observed.Progress.Revision);

        terminal.Progress.State.Should().Be(DelegationState.NeedsSupervisor);
        terminal.Result!.Evidence.TestsPassed.Should().Be(1);
        terminal.Result.Evidence.ReviewApproved.Should().BeFalse();
        terminal.Result.NormalizedEvidence!.Validations.Should().ContainSingle();
        terminal.Result.NormalizedEvidence.Reviews.Should().ContainSingle();
        terminal.Result.UnresolvedConcerns.Should().Contain("Independent review rejected the candidate.");
    }

    [Fact]
    public async Task Branch_fault_preserves_successful_sibling_evidence_and_is_non_success()
    {
        var validator = new ThrowingValidator();
        var reviewer = new FixedReviewer("approved");
        var (coordinator, _) = CreateCoordinator(validator, reviewer);
        var accepted = await coordinator.AcceptAsync(new DelegationCallerScope("caller"), Request("fault"));
        var observed = await RunToResultPhaseAsync(coordinator, accepted.DelegationId);
        var terminal = await coordinator.PumpAsync(accepted.DelegationId, observed.Progress.Revision);

        terminal.Progress.State.Should().Be(DelegationState.Failed);
        terminal.Result!.NormalizedEvidence!.Reviews.Should().ContainSingle();
        terminal.Result.NormalizedEvidence.Validations.Should().BeEmpty();
        terminal.Result.UnresolvedConcerns.Should().Contain(concern => concern.StartsWith("Deterministic validation fault:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Concurrent_duplicate_pumps_do_not_duplicate_evaluator_calls()
    {
        var validator = new FixedValidator("passed");
        var reviewer = new FixedReviewer("approved");
        var (coordinator, _) = CreateCoordinator(validator, reviewer);
        var accepted = await coordinator.AcceptAsync(new DelegationCallerScope("caller"), Request("duplicate"));
        var observed = await RunToResultPhaseAsync(coordinator, accepted.DelegationId);

        var first = coordinator.PumpAsync(accepted.DelegationId, observed.Progress.Revision).AsTask();
        var second = coordinator.PumpAsync(accepted.DelegationId, observed.Progress.Revision).AsTask();
        var results = await Task.WhenAll(first, second);
        results.Should().OnlyContain(result => result.Progress.State == DelegationState.Completed);
        validator.Calls.Should().Be(1);
        reviewer.Calls.Should().Be(1);
    }

    [Theory]
    [InlineData(CandidateMutation.WrongDelegation)]
    [InlineData(CandidateMutation.WrongNode)]
    [InlineData(CandidateMutation.WrongGeneration)]
    public async Task Candidate_owner_node_or_generation_mismatch_is_rejected_by_external_result(CandidateMutation mutation)
    {
        var provider = new CandidateProvider(mutation);
        var (coordinator, _) = CreateCoordinator(null, null, null, provider);
        var accepted = await coordinator.AcceptAsync(new DelegationCallerScope("caller"), Request("identity"));
        var observed = await RunToResultPhaseAsync(coordinator, accepted.DelegationId);

        var terminal = await coordinator.PumpAsync(accepted.DelegationId, observed.Progress.Revision);
        terminal.Progress.State.Should().Be(DelegationState.Failed);
        terminal.Result!.Summary.Should().Contain("result failed");
    }

    [Fact]
    public async Task Terminal_replay_returns_the_original_immutable_aggregate()
    {
        var (coordinator, _) = CreateCoordinator(new FixedValidator("passed"), new FixedReviewer("approved"));
        var accepted = await coordinator.AcceptAsync(new DelegationCallerScope("caller"), Request("replay"));
        var observed = await RunToResultPhaseAsync(coordinator, accepted.DelegationId);
        var terminal = await coordinator.PumpAsync(accepted.DelegationId, observed.Progress.Revision);
        var replay = await coordinator.GetAsync(accepted.DelegationId);

        replay.Progress.Should().BeSameAs(terminal.Progress);
        replay.Result.Should().BeSameAs(terminal.Result);
        replay.Result!.ResultReference.Should().BeSameAs(terminal.Result!.ResultReference);
    }

    [Fact]
    public async Task Provider_candidate_is_sealed_and_exposed_without_evaluator_configuration()
    {
        var (coordinator, _) = CreateCoordinator(null, null);
        var accepted = await coordinator.AcceptAsync(new DelegationCallerScope("caller"), Request("candidate-only"));
        var observed = await RunToResultPhaseAsync(coordinator, accepted.DelegationId);
        var terminal = await coordinator.PumpAsync(accepted.DelegationId, observed.Progress.Revision);

        terminal.Progress.State.Should().Be(DelegationState.Completed);
        terminal.Result!.Candidate.Should().NotBeNull();
        terminal.Result.ResultReference.Should().NotBeNull();
        terminal.Result.ResultReference!.Candidate.Should().BeSameAs(terminal.Result.Candidate);
    }

    [Fact]
    public async Task Hostile_candidate_registry_mismatch_is_terminal_failure()
    {
        var (coordinator, _) = CreateCoordinator(null, null, new HostileCandidateRegistry());
        var accepted = await coordinator.AcceptAsync(new DelegationCallerScope("caller"), Request("hostile-registry"));
        var observed = await RunToResultPhaseAsync(coordinator, accepted.DelegationId);
        var terminal = await coordinator.PumpAsync(accepted.DelegationId, observed.Progress.Revision);

        terminal.Progress.State.Should().Be(DelegationState.Failed);
        terminal.Result!.UnresolvedConcerns.Should().Contain(concern => concern.Contains("Candidate publication failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Durable_cancel_during_evaluation_prevents_late_terminal_publication()
    {
        var validator = new BlockingValidator();
        var reviewer = new BlockingReviewer();
        var (coordinator, _) = CreateCoordinator(validator, reviewer);
        var accepted = await coordinator.AcceptAsync(new DelegationCallerScope("caller"), Request("cancel-evaluation"));
        var observed = await RunToResultPhaseAsync(coordinator, accepted.DelegationId);
        var pump = coordinator.PumpAsync(accepted.DelegationId, observed.Progress.Revision).AsTask();
        await Task.WhenAll(validator.Started.Task, reviewer.Started.Task);

        var cancelled = await coordinator.CancelAsync(accepted.DelegationId, observed.Progress.Revision, "cancel-evaluation-1", "stop");
        cancelled.Progress.State.Should().NotBe(DelegationState.Completed);
        validator.Release();
        reviewer.Release();
        var afterLateCompletion = await pump;

        afterLateCompletion.Progress.State.Should().NotBe(DelegationState.Completed);
        var replay = await coordinator.GetAsync(accepted.DelegationId);
        replay.Progress.State.Should().NotBe(DelegationState.Completed);
    }

    [Fact]
    public async Task Insufficient_evaluator_budget_refuses_launch_with_budget_exceeded()
    {
        var validator = new FixedValidator("passed");
        var reviewer = new FixedReviewer("approved");
        var (coordinator, _) = CreateCoordinator(validator, reviewer);
        var accepted = await coordinator.AcceptAsync(new DelegationCallerScope("caller"), Request("evaluator-budget", 3));
        var observed = await RunToResultPhaseAsync(coordinator, accepted.DelegationId);
        var terminal = await coordinator.PumpAsync(accepted.DelegationId, observed.Progress.Revision);

        terminal.Progress.State.Should().Be(DelegationState.BudgetExceeded);
        terminal.Progress.WorkerCalls.Should().Be(3);
        terminal.Result!.BudgetExceeded.Should().NotBeNull();
        terminal.Result.BudgetExceeded!.Consumed.Value.Should().Be(3);
        terminal.Result.BudgetExceeded.Charge.Amount.Value.Should().Be(3);
        validator.Calls.Should().Be(0);
        reviewer.Calls.Should().Be(0);
    }

    private static async Task<DelegationExecutionSnapshot> RunToResultPhaseAsync(
        InMemoryDelegationCoordinator coordinator,
        DelegationId delegationId)
    {
        var queued = await coordinator.GetAsync(delegationId);
        var running = await coordinator.PumpAsync(delegationId, queued.Progress.Revision);
        return await coordinator.PumpAsync(delegationId, running.Progress.Revision);
    }

    private static (InMemoryDelegationCoordinator Coordinator, CandidateProvider Provider) CreateCoordinator(
        IDeterministicCandidateValidator? validator,
        IIndependentCandidateReviewer? reviewer,
        ICandidateRevisionPublicationRegistry? registry = null,
        CandidateProvider? provider = null)
    {
        var descriptor = new ProviderDescriptor("m26-provider", [new CapabilityDescriptor("agent.execute", 1)]);
        var providers = new InMemoryProviderRegistry();
        providers.Register(descriptor);
        provider ??= new CandidateProvider(CandidateMutation.None);
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
            now: () => Start,
            candidateRegistry: registry,
            candidateValidator: validator,
            candidateReviewer: reviewer), provider);
    }

    private static DelegationRequest Request(string key, int maximumWorkerCalls = 8) => new(
        key,
        "Implement the objective",
        new WorkspaceReference("local", "workspace", "revision"),
        ["The result is correct"],
        [],
        new DelegationBudget(maximumWorkerCalls, 2));

    public enum CandidateMutation { None, WrongDelegation, WrongNode, WrongGeneration }

    private sealed class CandidateProvider(CandidateMutation mutation) : IExternalOperationProvider
    {
        private ExternalOperationHandle? handle;
        public int ResultCalls { get; private set; }

        public async ValueTask<ExternalOperationStartReceipt> StartAsync(ExternalOperationStartRequest request, IExternalOperationHandleCaptureSink handleSink, CancellationToken cancellationToken = default)
        {
            handle = new ExternalOperationHandle(request.Correlation.Agent.Provider, "m26-handle", request.Correlation.Agent.ProtocolVersion, new ExternalOperationCorrelation(request.Correlation.DelegationId, request.Correlation.WorkflowRun, request.Correlation.StructuralNode, request.Correlation.NodeGeneration, request.Correlation.ExecutionAttemptId, request.Correlation.Agent, new ExternalTaskReference(request.Correlation.Agent.Provider, "m26-handle")));
            await handleSink.CaptureAsync(new ExternalOperationHandleCapture(handle, Start.AddMinutes(1)), cancellationToken);
            return new ExternalOperationStartReceipt(request.Identity, handle, ExternalOperationStartDisposition.Created, ExternalOperationState.Running, Start.AddMinutes(1));
        }

        public ValueTask<ExternalOperationObservation> ObserveAsync(ExternalOperationHandle operationHandle, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ExternalOperationObservation(operationHandle, 1, ExternalOperationState.Succeeded, Start.AddMinutes(2), resultAvailable: true));

        public ValueTask<ExternalOperationResult> GetResultAsync(ExternalOperationHandle operationHandle, CancellationToken cancellationToken = default)
        {
            ResultCalls++;
            var correlation = operationHandle.Correlation;
            var delegationId = mutation == CandidateMutation.WrongDelegation ? DelegationId.New() : correlation.DelegationId;
            var node = mutation == CandidateMutation.WrongNode ? new StructuralNodeReference("other") : correlation.StructuralNode;
            var generation = mutation == CandidateMutation.WrongGeneration ? new NodeGenerationId(Guid.NewGuid()) : correlation.NodeGeneration;
            var artifact = new DelegationArtifactReference(correlation.DelegationId, correlation.StructuralNode, correlation.NodeGeneration, "m26-provider", "repo", "candidate", "application/octet-stream", 1, "candidate", ArtifactContentIdentity.Sha256Bytes(new string('a', 64)));
            var candidate = new CandidateRevisionReference(delegationId, node, generation, new CandidateId(correlation.DelegationId.Value), 1, artifact.ContentIdentity, [artifact]);
            return ValueTask.FromResult(new ExternalOperationResult(operationHandle, ExternalOperationState.Succeeded, Start.AddMinutes(3), "done", [artifact], candidate: candidate));
        }

        public ValueTask<ExternalOperationCancellationReceipt> CancelAsync(ExternalOperationCancelRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ExternalOperationCancellationReceipt(request.Handle, request.CancellationKey, ExternalOperationCancellationDisposition.Requested, ExternalOperationState.CancellationRequested, Start.AddMinutes(4)));

        public ValueTask<ExternalOperationResumeReceipt> ResumeAsync(ExternalOperationResumeRequest request, IExternalOperationHandleCaptureSink handleSink, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingCandidateRegistry : ICandidateRevisionPublicationRegistry
    {
        private readonly InMemoryCandidateRevisionPublicationRegistry inner = new();
        public bool Published { get; private set; }
        public async ValueTask<CandidateRevisionPublication> PublishAsync(CandidateRevisionReference candidate, CancellationToken cancellationToken = default)
        {
            Published = true;
            return await inner.PublishAsync(candidate, cancellationToken);
        }
    }

    private sealed class HostileCandidateRegistry : ICandidateRevisionPublicationRegistry
    {
        public ValueTask<CandidateRevisionPublication> PublishAsync(CandidateRevisionReference candidate, CancellationToken cancellationToken = default)
        {
            var changed = new CandidateRevisionReference(
                candidate.DelegationId,
                candidate.StructuralNode,
                candidate.NodeGeneration,
                candidate.CandidateId,
                candidate.Revision + 1,
                candidate.ContentIdentity,
                candidate.Artifacts);
            return ValueTask.FromResult(new CandidateRevisionPublication(changed, true));
        }
    }

    private abstract class EvaluatorBase
    {
        protected static WorkerInvocationEvidence Invocation(
            WorkerInvocationEvidence implementation,
            CandidateRevisionReference candidate,
            string category,
            string provider,
            string attemptId,
            string profile,
            string model) => new(
                implementation.DelegationId,
                implementation.StructuralNode,
                implementation.NodeGeneration,
                category,
                new ProviderExecutionAttemptReference(provider, attemptId, "evaluator-handle"),
                "succeeded",
                Start.AddMinutes(4),
                Start.AddMinutes(5),
                category,
                profile,
                provider,
                model,
                model,
                [],
                candidate.Artifacts,
                candidate.Artifacts,
                candidate);
    }

    private sealed class FixedValidator(string outcome) : EvaluatorBase, IDeterministicCandidateValidator
    {
        public int Calls { get; private set; }
        public ValueTask<ValidationEvidence> ValidateAsync(CandidateValidationRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(new ValidationEvidence(Invocation(request.ImplementationInvocation, request.Candidate, "deterministic.validation", "validator", request.InvocationId, "deterministic", "validator-model"), outcome, []));
        }
    }

    private sealed class ThrowingValidator : EvaluatorBase, IDeterministicCandidateValidator
    {
        public ValueTask<ValidationEvidence> ValidateAsync(CandidateValidationRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("validator fault");
    }

    private sealed class FixedReviewer(string outcome) : EvaluatorBase, IIndependentCandidateReviewer
    {
        public int Calls { get; private set; }
        public ValueTask<ReviewEvidence> ReviewAsync(CandidateReviewRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            var invocation = Invocation(request.ImplementationInvocation, request.Candidate, EvidenceKinds.ModelExecution, "reviewer", request.InvocationId, "review", "review-model");
            var independence = new ReviewIndependenceEvidence(request.ImplementationInvocation.Attempt.AttemptId, request.InvocationId, IndependenceAssessment.Unknown, IndependenceAssessment.Different, IndependenceAssessment.Different, IndependenceAssessment.Different, IndependenceAssessment.Different);
            return ValueTask.FromResult(new ReviewEvidence(invocation, outcome, [], independence, request.Candidate, "reviewer"));
        }
    }

    private sealed class BlockingValidator : EvaluatorBase, IDeterministicCandidateValidator
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CandidateValidationRequest? Request { get; private set; }
        public ValueTask<ValidationEvidence> ValidateAsync(CandidateValidationRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            Started.TrySetResult();
            return AwaitAsync(request);
        }
        public void Release() => release.TrySetResult();
        private async ValueTask<ValidationEvidence> AwaitAsync(CandidateValidationRequest request)
        {
            await release.Task.ConfigureAwait(false);
            return new ValidationEvidence(Invocation(request.ImplementationInvocation, request.Candidate, "deterministic.validation", "validator", request.InvocationId, "deterministic", "validator-model"), "passed", []);
        }
    }

    private sealed class BlockingReviewer : EvaluatorBase, IIndependentCandidateReviewer
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CandidateReviewRequest? Request { get; private set; }
        public ValueTask<ReviewEvidence> ReviewAsync(CandidateReviewRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            Started.TrySetResult();
            return AwaitAsync(request);
        }
        public void Release() => release.TrySetResult();
        private async ValueTask<ReviewEvidence> AwaitAsync(CandidateReviewRequest request)
        {
            await release.Task.ConfigureAwait(false);
            var invocation = Invocation(request.ImplementationInvocation, request.Candidate, EvidenceKinds.ModelExecution, "reviewer", request.InvocationId, "review", "review-model");
            var independence = new ReviewIndependenceEvidence(request.ImplementationInvocation.Attempt.AttemptId, request.InvocationId, IndependenceAssessment.Unknown, IndependenceAssessment.Different, IndependenceAssessment.Different, IndependenceAssessment.Different, IndependenceAssessment.Different);
            return new ReviewEvidence(invocation, "approved", [], independence, request.Candidate, "reviewer");
        }
    }
}
