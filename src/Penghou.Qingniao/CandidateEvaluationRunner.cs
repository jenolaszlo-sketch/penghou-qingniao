namespace Penghou.Qingniao;

/// <summary>
/// Candidate verification for one delegation: evaluates validation and
/// review evidence, executes host verification policy verdicts, performs
/// bounded checkpoint-local re-execution, and publishes terminal outcomes.
/// Pure verification mechanics; pass criteria, review standards, and repair
/// budgets live in the host <see cref="ICandidateVerificationPolicy"/>.
/// </summary>
internal sealed class CandidateEvaluationRunner
{
    private readonly DelegationExecutionPublisher publisher;
    private readonly InMemoryDelegationExecutionStore executionStore;
    private readonly ICandidateRevisionPublicationRegistry? candidateRegistry;
    private readonly IDeterministicCandidateValidator? candidateValidator;
    private readonly IIndependentCandidateReviewer? candidateReviewer;
    private readonly ICandidateCorrector? candidateCorrector;
    private readonly ICandidateVerificationPolicy verificationPolicy;

    internal CandidateEvaluationRunner(
        DelegationExecutionPublisher publisher,
        InMemoryDelegationExecutionStore executionStore,
        ICandidateRevisionPublicationRegistry? candidateRegistry,
        IDeterministicCandidateValidator? candidateValidator,
        IIndependentCandidateReviewer? candidateReviewer,
        ICandidateCorrector? candidateCorrector,
        ICandidateVerificationPolicy verificationPolicy)
    {
        this.publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        this.executionStore = executionStore ?? throw new ArgumentNullException(nameof(executionStore));
        this.candidateRegistry = candidateRegistry;
        this.candidateValidator = candidateValidator;
        this.candidateReviewer = candidateReviewer;
        this.candidateCorrector = candidateCorrector;
        this.verificationPolicy = verificationPolicy ?? throw new ArgumentNullException(nameof(verificationPolicy));
    }

    internal async Task<DelegationExecutionSnapshot> RunEvaluationAndCorrectionAsync(
        InMemoryDelegationCoordinator.RuntimeState runtime,
        DelegationId delegationId)
    {
        var round = 1;
        var gateHeld = false;
        try
        {
            while (true)
            {
                if (gateHeld)
                {
                    runtime.Gate.Release();
                    gateHeld = false;
                }

                Task<CandidateEvaluationOutcome> evaluation;
                lock (runtime.EvaluationSync)
                {
                    runtime.EvaluationCancellation ??= new CancellationTokenSource();
                    runtime.EvaluationTask ??= EvaluateCandidateAsync(runtime, runtime.EvaluationCancellation.Token);
                    evaluation = runtime.EvaluationTask;
                }

                var outcome = await evaluation.ConfigureAwait(false);
                await runtime.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                gateHeld = true;
                var latest = await executionStore.GetAsync(delegationId, CancellationToken.None).ConfigureAwait(false);
                if (DelegationLifecycle.IsTerminal(latest.Progress.State))
                {
                    return latest;
                }

                if (runtime.CancellationKey is not null && InMemoryDelegationCoordinator.IsCancellationConfirmed(runtime))
                {
                    return await PublishEvaluationCancellationAsync(runtime, latest, outcome).ConfigureAwait(false);
                }

                if (runtime.CancellationKey is not null
                    && !runtime.CancellationReconciled)
                {
                    return latest;
                }

                if (runtime.Phase != InMemoryDelegationCoordinator.CoordinatorPhase.Evaluate)
                {
                    return latest;
                }

                var terminal = await HandleEvaluationOutcomeAsync(runtime, latest, outcome, round).ConfigureAwait(false);
                if (terminal is not null)
                {
                    return terminal;
                }

                // CorrectionStarted is reserved while the gate is held. No
                // competing pump can create a second correction invocation.
                var correction = runtime.CorrectionTask!;
                runtime.Gate.Release();
                gateHeld = false;
                CandidateCorrectionOutcome corrected;
                Exception? correctionFailure = null;
                try
                {
                    corrected = await correction.ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    corrected = null!;
                    correctionFailure = exception;
                }

                await runtime.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                gateHeld = true;
                latest = await executionStore.GetAsync(delegationId, CancellationToken.None).ConfigureAwait(false);
                if (DelegationLifecycle.IsTerminal(latest.Progress.State))
                {
                    return latest;
                }

                if (runtime.CancellationKey is not null && InMemoryDelegationCoordinator.IsCancellationConfirmed(runtime))
                {
                    return await PublishEvaluationCancellationAsync(runtime, latest, null).ConfigureAwait(false);
                }

                if (runtime.CancellationKey is not null
                    && !runtime.CancellationReconciled)
                {
                    return latest;
                }

                if (runtime.CancellationKey is not null && round == 1)
                {
                    var cancellationDecision = DecideRound(outcome, runtime, round);
                    if (cancellationDecision.Verdict == CandidateVerificationVerdict.RequestLocalReexecution)
                    {
                        // Cancellation is already pending; starting another
                        // correction round would outlive its supervision.
                        cancellationDecision = CandidateVerificationDecision.ContinueWithConstraint(
                            "Local re-execution was superseded by cancellation.",
                            cancellationDecision.Note);
                    }

                    return await PublishEvaluationTerminalAsync(
                            runtime,
                            latest,
                            outcome,
                            cancellationDecision,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }

                if (runtime.EvaluationCycle != round)
                {
                    // Another concurrent pump completed correction and
                    // reserved the fresh evaluator pair. Join that task.
                    continue;
                }

                if (correctionFailure is not null)
                {
                    runtime.Phase = InMemoryDelegationCoordinator.CoordinatorPhase.Complete;
                    return await publisher.PublishTerminalAsync(
                        runtime,
                        latest,
                        DelegationState.Failed,
                        "Candidate correction failed with an unclassified provider error.",
                        runtime.ResultArtifacts ?? [],
                        CancellationToken.None,
                        latest.Progress.WorkerCalls + 4).ConfigureAwait(false);
                }

                try
                {
                    await PublishCorrectedCandidateAsync(runtime, corrected).ConfigureAwait(false);
                    round++;
                    runtime.EvaluationCycle = round;
                    runtime.ValidationFailure = null;
                    runtime.ReviewFailure = null;
                    runtime.ValidationInvocationId = $"validation:{runtime.DelegationId.Value:D}:{round}";
                    runtime.ReviewInvocationId = $"review:{runtime.DelegationId.Value:D}:{round}";
                    runtime.EvaluationTask = null;
                    runtime.EvaluationCancellation = new CancellationTokenSource();
                    runtime.Phase = InMemoryDelegationCoordinator.CoordinatorPhase.Evaluate;
                }
                catch
                {
                    // Leave Candidate and ResultArtifacts pointing at the
                    // immutable previous publication on any bad output.
                    runtime.Phase = InMemoryDelegationCoordinator.CoordinatorPhase.Complete;
                    return await publisher.PublishTerminalAsync(
                        runtime,
                        latest,
                        DelegationState.Failed,
                        "Candidate correction output failed coordinator validation.",
                        runtime.ResultArtifacts ?? [],
                        CancellationToken.None,
                        latest.Progress.WorkerCalls + 4).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (gateHeld)
            {
                runtime.Gate.Release();
            }
        }
    }

    private async ValueTask<DelegationExecutionSnapshot> PublishEvaluationCancellationAsync(
        InMemoryDelegationCoordinator.RuntimeState runtime,
        DelegationExecutionSnapshot current,
        CandidateEvaluationOutcome? outcome)
    {
        if (outcome is not null)
        {
            RecordEvaluationEvidence(runtime, outcome);
        }

        runtime.AggregateEvidence = new EvidenceBundle(runtime.Invocations, runtime.Validations, runtime.Reviews);
        runtime.Phase = InMemoryDelegationCoordinator.CoordinatorPhase.Complete;
        return await publisher.PublishTerminalAsync(
            runtime,
            current,
            DelegationState.Cancelled,
            "Cancellation was requested while candidate evaluation was in progress.",
            runtime.ResultArtifacts ?? [],
            CancellationToken.None,
            DelegationExecutionPublisher.ActualWorkerCalls(runtime, current)).ConfigureAwait(false);
    }

    private async ValueTask<DelegationExecutionSnapshot?> HandleEvaluationOutcomeAsync(
        InMemoryDelegationCoordinator.RuntimeState runtime,
        DelegationExecutionSnapshot current,
        CandidateEvaluationOutcome outcome,
        int round)
    {
        if (round == 1 && runtime.CorrectionStarted)
        {
            // A duplicate pump is represented by the shared correction task;
            // do not record or publish the generation-1 outcome twice.
            return null;
        }

        RecordEvaluationEvidence(runtime, outcome);
        runtime.AggregateEvidence = new EvidenceBundle(runtime.Invocations, runtime.Validations, runtime.Reviews);

        var decision = DecideRound(outcome, runtime, round);

        switch (decision.Verdict)
        {
            case CandidateVerificationVerdict.Accept:
            case CandidateVerificationVerdict.Reject:
            case CandidateVerificationVerdict.ContinueWithConstraint:
                return await PublishEvaluationTerminalAsync(runtime, current, outcome, decision, CancellationToken.None)
                    .ConfigureAwait(false);
            case CandidateVerificationVerdict.RequestLocalReexecution:
                break;
            default:
                throw new InvalidOperationException($"The verification policy returned an unknown verdict '{decision.Verdict}'.");
        }

        if (candidateCorrector is null)
        {
            return await PublishEvaluationTerminalAsync(
                runtime,
                current,
                outcome,
                CandidateVerificationDecision.ContinueWithConstraint(
                    "Local re-execution was requested without a configured corrector.",
                    decision.Note),
                CancellationToken.None).ConfigureAwait(false);
        }

        if (round >= verificationPolicy.MaxVerificationRounds)
        {
            return await PublishEvaluationTerminalAsync(
                runtime,
                current,
                outcome,
                CandidateVerificationDecision.ContinueWithConstraint(
                    $"Candidate verification did not converge within {verificationPolicy.MaxVerificationRounds} rounds.",
                    decision.Note),
                CancellationToken.None).ConfigureAwait(false);
        }

        runtime.CorrectionStarted = true;
        runtime.CorrectionCancellation ??= new CancellationTokenSource();
        runtime.CorrectionTask ??= CorrectCandidateAsync(runtime, outcome, round, runtime.CorrectionCancellation.Token);
        return null;
    }

    private CandidateVerificationDecision DecideRound(
        CandidateEvaluationOutcome outcome,
        InMemoryDelegationCoordinator.RuntimeState runtime,
        int round)
    {
        var input = new CandidateVerificationInput(
            outcome.Validation,
            outcome.Review,
            runtime.ValidationFailure,
            runtime.ReviewFailure,
            round);
        input.Validate();
        return verificationPolicy.Decide(input)
            ?? throw new InvalidOperationException("The verification policy returned no decision.");
    }

    private void RecordEvaluationEvidence(
        InMemoryDelegationCoordinator.RuntimeState runtime,
        CandidateEvaluationOutcome outcome)
    {
        if (outcome.Validation is not null
            && !runtime.Validations.Any(existing => string.Equals(existing.Invocation.Attempt.AttemptId, outcome.Validation.Invocation.Attempt.AttemptId, StringComparison.Ordinal)))
        {
            runtime.Validations.Add(outcome.Validation);
        }

        if (outcome.Review is not null
            && !runtime.Reviews.Any(existing => string.Equals(existing.Invocation.Attempt.AttemptId, outcome.Review.Invocation.Attempt.AttemptId, StringComparison.Ordinal)))
        {
            runtime.Reviews.Add(outcome.Review);
        }

        if (runtime.ImplementationInvocation is not null
            && !runtime.Invocations.Any(invocation => string.Equals(invocation.Attempt.AttemptId, runtime.ImplementationInvocation.Attempt.AttemptId, StringComparison.Ordinal)))
        {
            runtime.Invocations.Add(runtime.ImplementationInvocation);
        }
    }

    private async Task<CandidateCorrectionOutcome> CorrectCandidateAsync(
        InMemoryDelegationCoordinator.RuntimeState runtime,
        CandidateEvaluationOutcome outcome,
        int round,
        CancellationToken cancellationToken)
    {
        var findings = new List<EvidenceFinding>();
        if (outcome.Validation is not null)
        {
            findings.AddRange(outcome.Validation.Findings);
        }

        if (outcome.Review is not null)
        {
            findings.AddRange(outcome.Review.Findings);
        }

        var targetGeneration = new NodeGenerationId(Guid.NewGuid());
        var targetCorrelation = new ExternalOperationCorrelation(
            runtime.Correlation.DelegationId,
            runtime.Correlation.WorkflowRun,
            runtime.Correlation.StructuralNode,
            targetGeneration,
            $"correction-{runtime.DelegationId.Value:D}",
            runtime.Correlation.Agent,
            runtime.Correlation.Task);
        runtime.CorrectionGeneration = targetGeneration;
        runtime.CorrectionCorrelation = targetCorrelation;
        var request = new CandidateCorrectionRequest(
            runtime.Candidate!,
            outcome.Validation,
            outcome.Review,
            findings,
            targetGeneration,
            checked(runtime.Candidate!.Revision + 1),
            $"correction:{runtime.DelegationId.Value:D}:{round}",
            targetCorrelation,
            $"correction:{runtime.DelegationId.Value:D}:{round}");
        runtime.CorrectionInvocationStarted = true;
        return await candidateCorrector!.CorrectAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask PublishCorrectedCandidateAsync(
        InMemoryDelegationCoordinator.RuntimeState runtime,
        CandidateCorrectionOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var candidate = outcome.Candidate ?? throw new InvalidOperationException("The corrector did not return a candidate.");
        var source = runtime.Candidate ?? throw new InvalidOperationException("The current candidate is unavailable.");
        if (candidate.DelegationId != source.DelegationId
            || candidate.StructuralNode != source.StructuralNode
            || candidate.CandidateId != source.CandidateId
            || candidate.Revision != checked(source.Revision + 1)
            || candidate.NodeGeneration == source.NodeGeneration
            || candidate.NodeGeneration != runtime.CorrectionGeneration)
        {
            throw new InvalidOperationException("The corrected candidate must preserve ownership and CandidateId while advancing exactly one revision and generation.");
        }

        if (candidate.Artifacts.Count == 0
            || candidate.ContentIdentity == source.ContentIdentity
            || candidate.Artifacts.Any(artifact => source.Artifacts.Any(existing =>
                ArtifactSame(existing, artifact) || existing.ContentIdentity == artifact.ContentIdentity)))
        {
            throw new InvalidOperationException("The corrected candidate must contain new artifacts.");
        }

        var invocation = outcome.ImplementationInvocation ?? throw new InvalidOperationException("The corrector did not return implementation evidence.");
        if (!CandidateRevisionIdentity.SubjectEqual(candidate, invocation.Candidate)
            || invocation.NodeGeneration != candidate.NodeGeneration
            || invocation.Attempt.AttemptId == runtime.ImplementationInvocation!.Attempt.AttemptId
            || runtime.CorrectionCorrelation is null
            || invocation.ExecutionCorrelation is null
            || !CorrelationEqual(invocation.ExecutionCorrelation, runtime.CorrectionCorrelation))
        {
            throw new InvalidOperationException("Corrected implementation evidence does not identify the exact fresh candidate and attempt.");
        }

        if (candidateRegistry is null)
        {
            throw new InvalidOperationException("Candidate correction requires a publication registry.");
        }

        var publication = await candidateRegistry.PublishAsync(candidate, CancellationToken.None).ConfigureAwait(false);
        if (!CandidateRevisionIdentity.SemanticallyEqual(publication.Candidate, candidate))
        {
            throw new InvalidOperationException("The candidate publication registry returned different corrected content.");
        }

        runtime.Candidate = publication.Candidate;
        if (candidate.Artifacts.Any(artifact => artifact.DelegationId != candidate.DelegationId
            || artifact.StructuralNode != candidate.StructuralNode
            || artifact.NodeGeneration != candidate.NodeGeneration))
        {
            throw new InvalidOperationException("Corrected implementation artifacts must belong to the corrected candidate generation.");
        }

        runtime.ResultArtifacts = candidate.Artifacts.ToArray();
        runtime.ImplementationInvocation = invocation;
        runtime.Invocations.Add(invocation);
    }

    private static bool ArtifactSame(DelegationArtifactReference left, DelegationArtifactReference right) =>
        string.Equals(left.Provider, right.Provider, StringComparison.Ordinal)
        && string.Equals(left.Repository, right.Repository, StringComparison.Ordinal)
        && string.Equals(left.ArtifactId, right.ArtifactId, StringComparison.Ordinal);

    private static bool CorrelationEqual(ExternalOperationCorrelation left, ExternalOperationCorrelation right) =>
        left.DelegationId == right.DelegationId
        && left.WorkflowRun == right.WorkflowRun
        && left.StructuralNode == right.StructuralNode
        && left.NodeGeneration == right.NodeGeneration
        && string.Equals(left.ExecutionAttemptId, right.ExecutionAttemptId, StringComparison.Ordinal)
        && left.Agent == right.Agent
        && left.Task == right.Task;

    private async Task<CandidateEvaluationOutcome> EvaluateCandidateAsync(
        InMemoryDelegationCoordinator.RuntimeState runtime,
        CancellationToken cancellationToken)
    {
        var validationTask = candidateValidator is null
            ? Task.FromResult<ValidationEvidence?>(null)
            : InvokeValidationAsync(runtime, cancellationToken);
        var reviewTask = candidateReviewer is null
            ? Task.FromResult<ReviewEvidence?>(null)
            : InvokeReviewAsync(runtime, cancellationToken);

        // Await each branch independently.  Awaiting WhenAll alone would
        // hide a successful branch when its sibling faults.
        await Task.WhenAll(validationTask, reviewTask).ConfigureAwait(false);
        return new CandidateEvaluationOutcome(
            validationTask.Result,
            reviewTask.Result,
            null,
            null);
    }

    private async Task<ValidationEvidence?> InvokeValidationAsync(
        InMemoryDelegationCoordinator.RuntimeState runtime,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref runtime.EvaluationCallsStarted);
        try
        {
            var request = new CandidateValidationRequest(
                runtime.Candidate!,
                runtime.ImplementationInvocation!,
                runtime.CorrectionCorrelation ?? runtime.Correlation,
                runtime.ValidationInvocationId!);
            var evidence = await candidateValidator!.ValidateAsync(request, cancellationToken).ConfigureAwait(false);
            ValidateValidationEvidence(runtime, evidence);
            return evidence;
        }
        catch (Exception exception)
        {
            runtime.ValidationFailure = exception;
            return null;
        }
    }

    private async Task<ReviewEvidence?> InvokeReviewAsync(
        InMemoryDelegationCoordinator.RuntimeState runtime,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref runtime.EvaluationCallsStarted);
        try
        {
            var request = new CandidateReviewRequest(
                runtime.Candidate!,
                runtime.ImplementationInvocation!,
                runtime.CorrectionCorrelation ?? runtime.Correlation,
                runtime.ReviewInvocationId!);
            var evidence = await candidateReviewer!.ReviewAsync(request, cancellationToken).ConfigureAwait(false);
            ValidateReviewEvidence(runtime, evidence);
            return evidence;
        }
        catch (Exception exception)
        {
            runtime.ReviewFailure = exception;
            return null;
        }
    }

    private async ValueTask<DelegationExecutionSnapshot> PublishEvaluationTerminalAsync(
        InMemoryDelegationCoordinator.RuntimeState runtime,
        DelegationExecutionSnapshot current,
        CandidateEvaluationOutcome outcome,
        CandidateVerificationDecision decision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decision);
        runtime.Phase = InMemoryDelegationCoordinator.CoordinatorPhase.Complete;
        runtime.AggregateEvidence = new EvidenceBundle(
            runtime.Invocations,
            runtime.Validations,
            runtime.Reviews);

        var validationClean = outcome.Validation is not null && runtime.ValidationFailure is null;
        var reviewClean = outcome.Review is not null && runtime.ReviewFailure is null;
        // Evidence booleans follow the verdict: acceptance implies passing
        // evidence, continuation implies a passing validation with a review
        // that needs supervision, and rejection implies a failed validation.
        // Content interpretation lives in the host policy verdict; the
        // mechanical presence checks below only record production faults.
        var validationPass = decision.Verdict != CandidateVerificationVerdict.Reject && validationClean;
        var reviewApprove = decision.Verdict == CandidateVerificationVerdict.Accept
            || (decision.Verdict == CandidateVerificationVerdict.Reject && reviewClean);
        var concerns = new List<string>();
        if (runtime.ValidationFailure is not null)
        {
            concerns.Add("Deterministic validation fault: unclassified provider error.");
        }
        else if (outcome.Validation is null)
        {
            concerns.Add("Deterministic validation evidence was not produced.");
        }
        else if (!validationPass)
        {
            concerns.Add("Deterministic validation failed.");
        }

        if (runtime.ReviewFailure is not null)
        {
            concerns.Add("Independent review fault: unclassified provider error.");
        }
        else if (outcome.Review is null && candidateReviewer is not null)
        {
            concerns.Add("Independent review evidence was not produced.");
        }
        else if (outcome.Review is not null && !reviewApprove)
        {
            concerns.Add("Independent review rejected the candidate.");
        }

        // The verdict selects the terminal state; the evidence above stays
        // mechanical (presence without failure). Product interpretation of
        // evidence content lives in the host verification policy.
        var state = decision.Verdict switch
        {
            CandidateVerificationVerdict.Accept => DelegationState.Completed,
            CandidateVerificationVerdict.Reject => DelegationState.Failed,
            CandidateVerificationVerdict.ContinueWithConstraint => DelegationState.NeedsSupervisor,
            _ => throw new InvalidOperationException($"The verification policy returned an unexecutable verdict '{decision.Verdict}'."),
        };
        if (decision.Note is not null)
        {
            concerns.Add(DelegationExecutionPublisher.NormalizeConcern(decision.Note));
        }

        if (decision.Verdict == CandidateVerificationVerdict.ContinueWithConstraint)
        {
            concerns.Add(DelegationExecutionPublisher.NormalizeConcern(decision.Constraint!));
        }

        // Mechanical and policy concerns overlap by design (both describe the
        // same rejection); the result contract forbids duplicates.
        var distinctConcerns = concerns.Distinct(StringComparer.Ordinal).ToArray();

        var summary = state switch
        {
            DelegationState.Completed => "Candidate verification accepted the candidate.",
            DelegationState.NeedsSupervisor => "Candidate verification raised a concern requiring supervision.",
            _ => "Candidate verification rejected the candidate.",
        };
        var evidence = new DelegationEvidence(
            [],
            [],
            validationPass ? 1 : 0,
            validationPass ? 0 : 1,
            reviewApprove,
            outcome.Review?.Findings.Count(finding => finding.Resolved) ?? 0);
        return await publisher.PublishTerminalAsync(
            runtime,
            current,
            state,
            summary,
            runtime.ResultArtifacts ?? [],
            cancellationToken,
            DelegationExecutionPublisher.ActualWorkerCalls(runtime, current),
            evidence: evidence,
            unresolvedConcerns: distinctConcerns).ConfigureAwait(false);
    }

    private static void ValidateValidationEvidence(
        InMemoryDelegationCoordinator.RuntimeState runtime,
        ValidationEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (!CandidateRevisionIdentity.SubjectEqual(runtime.Candidate, evidence.Candidate)
            || evidence.Invocation.Attempt.AttemptId != runtime.ValidationInvocationId
            || !evidence.Invocation.ExecutionCategory.StartsWith("deterministic.", StringComparison.Ordinal)
            || evidence.Invocation.Candidate is null)
        {
            throw new InvalidOperationException("Validation evidence does not identify the exact sealed candidate and invocation.");
        }
    }

    private static void ValidateReviewEvidence(
        InMemoryDelegationCoordinator.RuntimeState runtime,
        ReviewEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (!CandidateRevisionIdentity.SubjectEqual(runtime.Candidate, evidence.Candidate)
            || evidence.Invocation.Attempt.AttemptId != runtime.ReviewInvocationId
            || evidence.Independence.ImplementationInvocationId != runtime.ImplementationInvocation!.Attempt.AttemptId
            || evidence.Independence.ReviewInvocationId != runtime.ReviewInvocationId
            || string.Equals(evidence.Independence.ImplementationInvocationId, evidence.Independence.ReviewInvocationId, StringComparison.Ordinal)
            || evidence.Invocation.Attempt.AttemptId == runtime.ImplementationInvocation!.Attempt.AttemptId
            || evidence.Invocation.Candidate is null)
        {
            throw new InvalidOperationException("Review evidence does not prove an independent attempt over the exact sealed candidate.");
        }

        // Independence claims are checked against observable invocation
        // metadata whenever it exists.  A reviewer cannot self-report a
        // different provider/profile/model while using the same values.
        var implementation = runtime.ImplementationInvocation!;
        if (evidence.Independence.Provider == IndependenceAssessment.Different
            && string.Equals(evidence.Invocation.Attempt.Provider, implementation.Attempt.Provider, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Review independence claims a different provider but both attempts use the same provider.");
        }

        if (evidence.Independence.Profile == IndependenceAssessment.Different
            && string.Equals(evidence.Invocation.Profile, implementation.Profile, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Review independence claims a different profile but invocation metadata matches.");
        }

        if (evidence.Independence.Model == IndependenceAssessment.Different
            && string.Equals(evidence.Invocation.ResolvedModel, implementation.ResolvedModel, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Review independence claims a different model but invocation metadata matches.");
        }
    }
}
