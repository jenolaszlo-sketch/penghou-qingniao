using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Penghou.Qingniao;

/// <summary>The versioned, deterministic identity of a supervisor intervention.</summary>
public readonly record struct SupervisorInterventionFingerprint
{
    /// <summary>Current canonical fingerprint format version.</summary>
    public const string CurrentVersion = "v1";
    /// <summary>Creates a validated intervention fingerprint.</summary>
    public SupervisorInterventionFingerprint(string version, string hash)
    {
        Version = RequireVersion(version); Hash = RequireHash(hash);
    }
    /// <summary>Gets the fingerprint format version.</summary>
    public string Version { get; }
    /// <summary>Gets the lowercase SHA-256 hexadecimal digest.</summary>
    public string Hash { get; }
    /// <summary>Validates this fingerprint's version and digest.</summary>
    public void Validate() { RequireVersion(Version); RequireHash(Hash); }
    /// <summary>Returns the stable <c>version:hash</c> representation.</summary>
    public override string ToString() => $"{Version}:{Hash}";
    private static string RequireVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 32 || !IsAsciiLowercaseLetterOrDigit(value[0])
            || value.Any(character => !IsAsciiLowercaseLetterOrDigit(character) && character is not ('.' or '-' or '_')))
            throw new ArgumentException("Intervention fingerprint version must be a bounded lowercase ASCII label.", nameof(value));
        return value;
    }
    private static string RequireHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character) || char.IsUpper(character)))
            throw new ArgumentException("Intervention fingerprint hash must contain 64 lowercase hexadecimal characters.", nameof(value));
        return value;
    }
    private static bool IsAsciiLowercaseLetterOrDigit(char value) => value is >= 'a' and <= 'z' or >= '0' and <= '9';
}

/// <summary>Canonicalizes interventions and computes their stable SHA-256 identity.</summary>
public static class SupervisorInterventionIdentity
{
    /// <summary>Computes the deterministic identity of an intervention.</summary>
    public static SupervisorInterventionFingerprint Compute(SupervisorIntervention intervention)
    {
        ArgumentNullException.ThrowIfNull(intervention);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonicalize(intervention)))).ToLowerInvariant();
        return new SupervisorInterventionFingerprint(SupervisorInterventionFingerprint.CurrentVersion, hash);
    }

    /// <summary>Serializes an intervention as compact, deterministic canonical JSON.</summary>
    public static string Canonicalize(SupervisorIntervention intervention)
    {
        ArgumentNullException.ThrowIfNull(intervention); intervention.Validate();
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject(); writer.WritePropertyName("action"); WriteAction(writer, intervention.Action);
            writer.WriteString("checkpointId", intervention.CheckpointId.Value.ToString("D"));
            writer.WriteString("delegationId", intervention.DelegationId.Value.ToString("D"));
            writer.WriteNumber("expectedRevision", intervention.ExpectedRevision); writer.WriteEndObject(); writer.Flush();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteAction(Utf8JsonWriter writer, SupervisorAction action)
    {
        writer.WriteStartObject(); writer.WriteString("kind", action.Kind);
        switch (action)
        {
            case SupervisorAction.Respond value: writer.WriteString("response", value.Response); break;
            case SupervisorAction.Approve value: if (value.Rationale is null) writer.WriteNull("rationale"); else writer.WriteString("rationale", value.Rationale); break;
            case SupervisorAction.Reject value: writer.WriteString("reason", value.Reason); break;
            case SupervisorAction.Retry value: writer.WriteString("reason", value.Reason); break;
            case SupervisorAction.ReexecuteNode value: writer.WriteString("target", value.Target.Identifier); writer.WriteString("reason", value.Reason); break;
            case SupervisorAction.ReexecuteSubgraph value: writer.WriteString("root", value.Root.Identifier); writer.WriteString("reason", value.Reason); break;
            case SupervisorAction.AddConstraint value: writer.WriteString("constraint", value.Constraint); break;
            case SupervisorAction.ChangeExecutor value:
                writer.WritePropertyName("profile"); writer.WriteStartObject(); writer.WriteString("provider", value.Profile.Provider); writer.WriteString("profile", value.Profile.Profile); writer.WriteEndObject(); writer.WriteString("reason", value.Reason); break;
            case SupervisorAction.SelectAlternative value: writer.WriteString("alternativeId", value.AlternativeId); writer.WriteString("rationale", value.Rationale); break;
            case SupervisorAction.Escalate value: writer.WriteString("reason", value.Reason); break;
            case SupervisorAction.Cancel value: writer.WriteString("reason", value.Reason); break;
            default: throw new ArgumentOutOfRangeException(nameof(action), "Unknown supervisor action type.");
        }
        writer.WriteEndObject();
    }
}

/// <summary>Reasons an intervention or checkpoint authorization can be rejected.</summary>
public enum SupervisorInterventionRejectionReason
{
    /// <summary>The checkpoint is not active.</summary>
    CheckpointNotActive = 0,
    /// <summary>The supervisor was not authorized.</summary>
    UnauthorizedCheckpoint = 1,
    /// <summary>The intervention revision is stale.</summary>
    StaleRevision = 2,
    /// <summary>The intervention key is bound to different content.</summary>
    ConflictingInterventionKey = 3,
    /// <summary>The checkpoint already has a decision.</summary>
    CheckpointAlreadyDecided = 4,
    /// <summary>The activation conflicts with the existing checkpoint.</summary>
    ConflictingCheckpointActivation = 5,
    /// <summary>The checkpoint revision moved backwards.</summary>
    CheckpointRevisionRegression = 6,
    /// <summary>The checkpoint reached its authorization limit.</summary>
    AuthorizedSupervisorLimitExceeded = 7,
}

/// <summary>Raised when checkpoint authorization or intervention acceptance fails.</summary>
public sealed class SupervisorInterventionRejectedException : InvalidOperationException
{
    /// <summary>Initializes a rejection with its stable reason code.</summary>
    public SupervisorInterventionRejectedException(SupervisorInterventionRejectionReason reason, string message) : base(message) => Reason = reason;
    /// <summary>Gets the machine-readable rejection reason.</summary>
    public SupervisorInterventionRejectionReason Reason { get; }
}

/// <summary>Receipt returned for a newly accepted or idempotently replayed intervention.</summary>
public sealed record SupervisorInterventionAcceptance
{
    /// <summary>Creates a validated intervention acceptance receipt.</summary>
    public SupervisorInterventionAcceptance(Guid receiptId, DelegationId delegationId, SupervisorCheckpointId checkpointId, long expectedRevision, SupervisorIdentity supervisor, SupervisorInterventionFingerprint fingerprint, bool isNew)
    {
        if (receiptId == Guid.Empty) throw new ArgumentException("A receipt identifier cannot be empty.", nameof(receiptId));
        if (delegationId.Value == Guid.Empty) throw new ArgumentException("A delegation identifier cannot be empty.", nameof(delegationId));
        checkpointId.Validate(); ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision); ArgumentNullException.ThrowIfNull(supervisor); supervisor.Validate(); fingerprint.Validate();
        ReceiptId = receiptId; DelegationId = delegationId; CheckpointId = checkpointId; ExpectedRevision = expectedRevision; Supervisor = supervisor; Fingerprint = fingerprint; IsNew = isNew;
    }
    /// <summary>Gets the unique receipt identifier.</summary>
    public Guid ReceiptId { get; }
    /// <summary>Gets the delegation receiving the intervention.</summary>
    public DelegationId DelegationId { get; }
    /// <summary>Gets the checkpoint to which the intervention applies.</summary>
    public SupervisorCheckpointId CheckpointId { get; }
    /// <summary>Gets the progress revision fenced by the intervention.</summary>
    public long ExpectedRevision { get; }
    /// <summary>Gets the authorized supervisor identity.</summary>
    public SupervisorIdentity Supervisor { get; }
    /// <summary>Gets the deterministic intervention fingerprint.</summary>
    public SupervisorInterventionFingerprint Fingerprint { get; }
    /// <summary>Gets whether this call created the decision rather than replaying it.</summary>
    public bool IsNew { get; }
}

/// <summary>Authorizes supervisors and accepts at most one fenced intervention per checkpoint.</summary>
public interface ISupervisorInterventionAcceptanceRegistry
{
    /// <summary>
    /// Registers a host-authorized supervisor for a validated waiting
    /// checkpoint. This is a trusted boundary; supervisor identity must never
    /// be derived from untrusted request or model payloads.
    /// </summary>
    ValueTask ActivateAsync(SupervisorIdentity supervisor, DelegationProgress waitingProgress, CancellationToken cancellationToken = default);
    /// <summary>Accepts one authorized intervention, or returns its identical replay receipt.</summary>
    ValueTask<SupervisorInterventionAcceptance> AcceptAsync(SupervisorIdentity supervisor, SupervisorIntervention intervention, CancellationToken cancellationToken = default);
}

/// <summary>Atomic in-memory proof of authorization, fencing, and global single-assignment.</summary>
public sealed class InMemorySupervisorInterventionAcceptanceRegistry : ISupervisorInterventionAcceptanceRegistry
{
    private const int MaximumAuthorizedSupervisors = 32;
    private readonly object _gate = new();
    private readonly Dictionary<SupervisorCheckpointId, ActiveCheckpoint> _active = new();

    /// <summary>Activates or refreshes authorization for a validated waiting checkpoint.</summary>
    public ValueTask ActivateAsync(SupervisorIdentity supervisor, DelegationProgress waitingProgress, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(supervisor); ArgumentNullException.ThrowIfNull(waitingProgress); supervisor.Validate();
        try { DelegationLifecycle.ValidateProgress(waitingProgress); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) { throw Reject(SupervisorInterventionRejectionReason.ConflictingCheckpointActivation, exception.Message); }
        if (waitingProgress.State != DelegationState.WaitingForSupervisor || waitingProgress.Checkpoint is null) throw Reject(SupervisorInterventionRejectionReason.CheckpointNotActive, "Only a WaitingForSupervisor progress snapshot can activate a checkpoint.");
        var checkpointId = waitingProgress.Checkpoint.CheckpointId;
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_active.TryGetValue(checkpointId, out var active)) { _active.Add(checkpointId, new ActiveCheckpoint(waitingProgress, supervisor)); return ValueTask.CompletedTask; }
            if (active.Decision is not null) throw Reject(SupervisorInterventionRejectionReason.CheckpointAlreadyDecided, "A decided checkpoint cannot be refreshed or receive new authorization.");
            if (waitingProgress.Revision < active.Progress.Revision) throw Reject(SupervisorInterventionRejectionReason.CheckpointRevisionRegression, "Checkpoint progress cannot move backwards.");
            try { DelegationLifecycle.ValidateProgress(waitingProgress, active.Progress); }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) { throw Reject(SupervisorInterventionRejectionReason.ConflictingCheckpointActivation, exception.Message); }
            var principal = new SupervisorPrincipal(supervisor);
            if (!active.Supervisors.Contains(principal)
                && active.Supervisors.Count >= MaximumAuthorizedSupervisors)
            {
                throw Reject(
                    SupervisorInterventionRejectionReason.AuthorizedSupervisorLimitExceeded,
                    $"A checkpoint cannot authorize more than {MaximumAuthorizedSupervisors} supervisors.");
            }

            active.Progress = waitingProgress;
            active.Supervisors.Add(principal);
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>Accepts one intervention atomically with checkpoint fencing and single assignment.</summary>
    public ValueTask<SupervisorInterventionAcceptance> AcceptAsync(SupervisorIdentity supervisor, SupervisorIntervention intervention, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(supervisor); ArgumentNullException.ThrowIfNull(intervention); supervisor.Validate(); intervention.Validate();
        var fingerprint = SupervisorInterventionIdentity.Compute(intervention);
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_active.TryGetValue(intervention.CheckpointId, out var active)) throw Reject(SupervisorInterventionRejectionReason.CheckpointNotActive, "The requested checkpoint is not active.");
            var principal = new SupervisorPrincipal(supervisor);
            if (active.Decision is not null)
            {
                if (active.Decision.Principal == principal && active.Decision.InterventionKey == intervention.InterventionKey)
                {
                    if (active.Decision.Fingerprint != fingerprint)
                    {
                        throw Reject(SupervisorInterventionRejectionReason.ConflictingInterventionKey, "The intervention key is already bound to different content.");
                    }

                    var replayReceipt = active.Decision.Receipt;
                    return ValueTask.FromResult(new SupervisorInterventionAcceptance(replayReceipt.ReceiptId, replayReceipt.DelegationId, replayReceipt.CheckpointId, replayReceipt.ExpectedRevision, replayReceipt.Supervisor, replayReceipt.Fingerprint, false));
                }
                throw Reject(SupervisorInterventionRejectionReason.CheckpointAlreadyDecided, "The active checkpoint already has a different accepted decision.");
            }
            if (!active.Supervisors.Contains(principal)) throw Reject(SupervisorInterventionRejectionReason.UnauthorizedCheckpoint, "The supervisor is not authorized for the requested active checkpoint.");
            if (active.Progress.State != DelegationState.WaitingForSupervisor || active.Progress.Checkpoint is null) throw Reject(SupervisorInterventionRejectionReason.CheckpointNotActive, "The requested checkpoint is no longer waiting for a supervisor.");
            if (active.Progress.Checkpoint.DelegationId != intervention.DelegationId) throw Reject(SupervisorInterventionRejectionReason.CheckpointNotActive, "The intervention delegation does not match the active checkpoint.");
            if (active.Progress.Revision != intervention.ExpectedRevision) throw Reject(SupervisorInterventionRejectionReason.StaleRevision, "The intervention expected revision does not match the active checkpoint.");
            var receipt = new SupervisorInterventionAcceptance(Guid.NewGuid(), intervention.DelegationId, intervention.CheckpointId, intervention.ExpectedRevision, supervisor, fingerprint, true);
            active.Decision = new AcceptedDecision(principal, intervention.InterventionKey, fingerprint, receipt);
            return ValueTask.FromResult(receipt);
        }
    }

    private static SupervisorInterventionRejectedException Reject(SupervisorInterventionRejectionReason reason, string message) => new(reason, message);
    private sealed class ActiveCheckpoint
    {
        public ActiveCheckpoint(DelegationProgress progress, SupervisorIdentity supervisor) { Progress = progress; Supervisors = new HashSet<SupervisorPrincipal> { new(supervisor) }; }
        public DelegationProgress Progress { get; set; }
        public HashSet<SupervisorPrincipal> Supervisors { get; }
        public AcceptedDecision? Decision { get; set; }
    }
    private readonly record struct SupervisorPrincipal(string AuthorityScope, string Subject) { public SupervisorPrincipal(SupervisorIdentity identity) : this(identity.AuthorityScope, identity.Subject) { } }
    private sealed record AcceptedDecision(SupervisorPrincipal Principal, string InterventionKey, SupervisorInterventionFingerprint Fingerprint, SupervisorInterventionAcceptance Receipt);
}
