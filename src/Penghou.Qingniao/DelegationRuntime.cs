namespace Penghou.Qingniao;

/// <summary>
/// Public host façade over one in-memory delegation runtime. This is the
/// entry point hosts use to run bounded delegated executions: it implements
/// <see cref="IDelegationService"/> for the delegation lifecycle and exposes
/// the operational surface (pump, resume, intervene, wake hints, checkpoint
/// context) that drives executions to completion. All policy lives in the
/// injected host seams; this type only wires and delegates.
/// </summary>
public sealed class DelegationRuntime : IDelegationService
{
    private readonly InMemoryDelegationCoordinator coordinator;

    /// <summary>
    /// Initializes a delegation runtime. Registries, the adapter catalog, and
    /// host policy seams follow the same rules as the underlying coordinator:
    /// acceptance and intervention registries default to in-memory, the
    /// admission verifier defaults to admit-all, and a verification policy is
    /// required whenever candidate evaluators are configured.
    /// </summary>
    public DelegationRuntime(
        IDelegationAcceptanceRegistry acceptanceRegistry,
        IDelegationAdmissionVerifier? admissionVerifier,
        IProviderRegistry providerRegistry,
        InMemoryExternalOperationProviderCatalog adapterCatalog,
        IExternalOperationSemanticFingerprintVerifier? fingerprintVerifier = null,
        Func<DateTimeOffset>? now = null,
        ISupervisorContextProvider? contextProvider = null,
        ISupervisorInterventionAcceptanceRegistry? interventionRegistry = null,
        ICandidateRevisionPublicationRegistry? candidateRegistry = null,
        IDeterministicCandidateValidator? candidateValidator = null,
        IIndependentCandidateReviewer? candidateReviewer = null,
        ICandidateCorrector? candidateCorrector = null,
        ICandidateVerificationPolicy? verificationPolicy = null) =>
        coordinator = new InMemoryDelegationCoordinator(
            acceptanceRegistry,
            admissionVerifier,
            providerRegistry,
            adapterCatalog,
            fingerprintVerifier,
            null,
            null,
            now,
            contextProvider,
            interventionRegistry,
            candidateRegistry,
            candidateValidator,
            candidateReviewer,
            candidateCorrector,
            verificationPolicy);

    /// <summary>Accepts a request and returns its delegation handle.</summary>
    public async Task<DelegationHandle> DelegateAsync(
        DelegationCallerScope caller,
        DelegationRequest request,
        CancellationToken cancellationToken = default)
    {
        var acceptance = await coordinator.AcceptAsync(caller, request, cancellationToken).ConfigureAwait(false);
        var snapshot = await coordinator.GetAsync(acceptance.DelegationId, cancellationToken).ConfigureAwait(false);
        return new DelegationHandle(
            acceptance.DelegationId,
            new WorkflowReference("qingniao", acceptance.DelegationId.Value.ToString("N")),
            snapshot.Progress.State);
    }

    /// <summary>Reads the current progress, or <see langword="null"/> for an unknown delegation.</summary>
    public async Task<DelegationProgress?> GetStatusAsync(
        DelegationId id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return (await coordinator.GetAsync(id, cancellationToken).ConfigureAwait(false)).Progress;
        }
        catch (DelegationExecutionNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Reads the terminal result, or <see langword="null"/> while not terminal or unknown.</summary>
    public async Task<DelegationResult?> GetResultAsync(
        DelegationId id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return (await coordinator.GetAsync(id, cancellationToken).ConfigureAwait(false)).Result;
        }
        catch (DelegationExecutionNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Requests durable cancellation. The cancellation key is stable per
    /// delegation, so repeated calls replay idempotently; unknown
    /// delegations throw <see cref="InvalidOperationException"/>.
    /// </summary>
    public async Task CancelAsync(
        DelegationId id,
        CancellationToken cancellationToken = default)
    {
        DelegationExecutionSnapshot current;
        try
        {
            current = await coordinator.GetAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (DelegationExecutionNotFoundException exception)
        {
            throw new InvalidOperationException($"Unknown delegation '{id}'.", exception);
        }

        await coordinator.CancelAsync(
            id,
            current.Progress.Revision,
            $"cancel:{id.Value:D}",
            "Cancellation was requested through the delegation service.",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Accepts a request and publishes its initial queued snapshot.</summary>
    public ValueTask<DelegationAcceptance> AcceptAsync(
        DelegationCallerScope caller,
        DelegationRequest request,
        CancellationToken cancellationToken = default) =>
        coordinator.AcceptAsync(caller, request, cancellationToken);

    /// <summary>Reads the immutable observable execution snapshot.</summary>
    public ValueTask<DelegationExecutionSnapshot> GetAsync(
        DelegationId delegationId,
        CancellationToken cancellationToken = default) =>
        coordinator.GetAsync(delegationId, cancellationToken);

    /// <summary>
    /// Advances exactly one logical operation after an expected revision. A
    /// stale revision is rejected so concurrent callers cannot silently pump
    /// the same execution twice.
    /// </summary>
    public ValueTask<DelegationExecutionSnapshot> PumpAsync(
        DelegationId delegationId,
        long expectedRevision,
        CancellationToken cancellationToken = default) =>
        coordinator.PumpAsync(delegationId, expectedRevision, cancellationToken);

    /// <summary>Reconnects a waiting delegation to a provider handle.</summary>
    public ValueTask<DelegationExecutionSnapshot> ResumeAsync(
        DelegationId delegationId,
        long expectedRevision,
        string resumeKey,
        IReadOnlyList<DelegationArtifactReference>? correctionArtifacts = null,
        string? reason = null,
        CancellationToken cancellationToken = default) =>
        coordinator.ResumeAsync(delegationId, expectedRevision, resumeKey, correctionArtifacts, reason, cancellationToken);

    /// <summary>Activates the current waiting checkpoint for supervision.</summary>
    public ValueTask ActivateCheckpointAsync(
        DelegationId delegationId,
        SupervisorIdentity supervisor,
        CancellationToken cancellationToken = default) =>
        coordinator.ActivateCheckpointAsync(delegationId, supervisor, cancellationToken);

    /// <summary>Reads the non-authorizing wake hint published for a waiting operation.</summary>
    public ValueTask<WakeHint?> GetWakeHintAsync(
        DelegationId delegationId,
        CancellationToken cancellationToken = default) =>
        coordinator.GetWakeHintAsync(delegationId, cancellationToken);

    /// <summary>Gets bounded context for the exact current waiting checkpoint fence.</summary>
    public ValueTask<SupervisorContextPackage> GetContextAsync(
        SupervisorIdentity supervisor,
        SupervisorContextRequest request,
        CancellationToken cancellationToken = default) =>
        coordinator.GetContextAsync(supervisor, request, cancellationToken);

    /// <summary>Accepts and applies a supervisor intervention.</summary>
    public ValueTask<DelegationExecutionSnapshot> InterveneAsync(
        SupervisorIdentity supervisor,
        SupervisorIntervention intervention,
        CancellationToken cancellationToken = default) =>
        coordinator.InterveneAsync(supervisor, intervention, cancellationToken);

    /// <summary>Accepts and applies a supervisor intervention for one delegation.</summary>
    public ValueTask<DelegationExecutionSnapshot> ApplyInterventionAsync(
        DelegationId delegationId,
        SupervisorIdentity supervisor,
        SupervisorIntervention intervention,
        CancellationToken cancellationToken = default) =>
        coordinator.ApplyInterventionAsync(delegationId, supervisor, intervention, cancellationToken);
}
