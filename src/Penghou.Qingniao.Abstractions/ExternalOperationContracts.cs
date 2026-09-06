namespace Penghou.Qingniao;

/// <summary>Stable identity of the external agent selected for one operation.</summary>
public sealed record ExternalAgentReference
{
    /// <summary>
    /// Initializes a new instance of the ExternalAgentReference type.
    /// </summary>
    public ExternalAgentReference(string provider, string identifier, string protocolVersion)
    {
        Provider = IdentityText.Require(provider, nameof(provider), 128);
        Identifier = IdentityText.Require(identifier, nameof(identifier), 512);
        ProtocolVersion = ArtifactContracts.Version(protocolVersion, nameof(protocolVersion));
    }

    /// <summary>
    /// Gets the Provider value.
    /// </summary>
    public string Provider { get; }
    /// <summary>
    /// Gets the Identifier value.
    /// </summary>
    public string Identifier { get; }
    /// <summary>
    /// Gets the ProtocolVersion value.
    /// </summary>
    public string ProtocolVersion { get; }

    /// <summary>
    /// Validates this contract value and throws when an invariant is violated.
    /// </summary>
    public void Validate()
    {
        IdentityText.Require(Provider, nameof(Provider), 128);
        IdentityText.Require(Identifier, nameof(Identifier), 512);
        ArtifactContracts.Version(ProtocolVersion, nameof(ProtocolVersion));
    }
}

/// <summary>Provider-issued stable task identity. This is not a transient connection id.</summary>
public sealed record ExternalTaskReference
{
    /// <summary>
    /// Initializes a new instance of the ExternalTaskReference type.
    /// </summary>
    public ExternalTaskReference(string provider, string identifier)
    {
        Provider = IdentityText.Require(provider, nameof(provider), 128);
        Identifier = IdentityText.Require(identifier, nameof(identifier), 2_048);
    }

    /// <summary>
    /// Gets the Provider value.
    /// </summary>
    public string Provider { get; }
    /// <summary>
    /// Gets the Identifier value.
    /// </summary>
    public string Identifier { get; }

    /// <summary>
    /// Validates this contract value and throws when an invariant is violated.
    /// </summary>
    public void Validate()
    {
        IdentityText.Require(Provider, nameof(Provider), 128);
        IdentityText.Require(Identifier, nameof(Identifier), 2_048);
    }
}

/// <summary>
/// Correlates a provider operation with the exact Qingniao execution identity.
/// The attempt id remains stable across reconnect and retry of observations;
/// semantic re-execution must create a new node generation and attempt.
/// </summary>
public sealed record ExternalOperationCorrelation
{
    /// <summary>
    /// Initializes a new instance of the ExternalOperationCorrelation type.
    /// </summary>
    public ExternalOperationCorrelation(
        DelegationId delegationId,
        WorkflowRunExecutionReference workflowRun,
        StructuralNodeReference structuralNode,
        NodeGenerationId nodeGeneration,
        string executionAttemptId,
        ExternalAgentReference agent,
        ExternalTaskReference? task = null)
    {
        ArtifactContracts.RequireDelegation(delegationId, nameof(delegationId));
        ArgumentNullException.ThrowIfNull(workflowRun);
        workflowRun.Validate();
        structuralNode.Validate();
        nodeGeneration.Validate();
        ExecutionAttemptId = IdentityText.Require(executionAttemptId, nameof(executionAttemptId), 512);
        ArgumentNullException.ThrowIfNull(agent);
        agent.Validate();
        task?.Validate();
        if (task is not null && !string.Equals(task.Provider, agent.Provider, StringComparison.Ordinal))
        {
            throw new ArgumentException("An external task must belong to the correlated external agent provider.", nameof(task));
        }

        DelegationId = delegationId;
        WorkflowRun = workflowRun;
        StructuralNode = structuralNode;
        NodeGeneration = nodeGeneration;
        Agent = agent;
        Task = task;
    }

    /// <summary>
    /// Gets the DelegationId value.
    /// </summary>
    public DelegationId DelegationId { get; }
    /// <summary>
    /// Gets the WorkflowRun value.
    /// </summary>
    public WorkflowRunExecutionReference WorkflowRun { get; }
    /// <summary>
    /// Gets the StructuralNode value.
    /// </summary>
    public StructuralNodeReference StructuralNode { get; }
    /// <summary>
    /// Gets the NodeGeneration value.
    /// </summary>
    public NodeGenerationId NodeGeneration { get; }
    /// <summary>
    /// Gets the ExecutionAttemptId value.
    /// </summary>
    public string ExecutionAttemptId { get; }
    /// <summary>
    /// Gets the Agent value.
    /// </summary>
    public ExternalAgentReference Agent { get; }
    /// <summary>
    /// Gets the Task value.
    /// </summary>
    public ExternalTaskReference? Task { get; }

    /// <summary>
    /// Validates this contract value and throws when an invariant is violated.
    /// </summary>
    public void Validate()
    {
        ArtifactContracts.RequireDelegation(DelegationId, nameof(DelegationId));
        WorkflowRun.Validate();
        StructuralNode.Validate();
        NodeGeneration.Validate();
        IdentityText.Require(ExecutionAttemptId, nameof(ExecutionAttemptId), 512);
        Agent.Validate();
        Task?.Validate();
        if (Task is not null && !string.Equals(Task.Provider, Agent.Provider, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("An external task must belong to the correlated external agent provider.");
        }
    }

    /// <summary>
    /// Performs the EnsureTaskCaptured contract operation.
    /// </summary>
    public void EnsureTaskCaptured()
    {
        if (Task is null)
        {
            throw new InvalidOperationException(
                "A durable external operation requires a provider-issued task identity before reconnecting.");
        }
    }
}

/// <summary>
/// Idempotency identity for a start request. The semantic fingerprint covers
/// all start inputs but deliberately excludes the caller's transport details.
/// </summary>
public sealed record ExternalOperationStartIdentity
{
    /// <summary>
    /// Initializes a new instance of the ExternalOperationStartIdentity type.
    /// </summary>
    public ExternalOperationStartIdentity(
        DelegationId delegationId,
        WorkflowRunExecutionReference workflowRun,
        StructuralNodeReference structuralNode,
        NodeGenerationId nodeGeneration,
        string executionAttemptId,
        string idempotencyKey,
        string semanticFingerprint)
    {
        ArtifactContracts.RequireDelegation(delegationId, nameof(delegationId));
        ArgumentNullException.ThrowIfNull(workflowRun);
        workflowRun.Validate();
        structuralNode.Validate();
        nodeGeneration.Validate();
        ExecutionAttemptId = IdentityText.Require(executionAttemptId, nameof(executionAttemptId), 512);
        IdempotencyKey = IdentityText.Require(idempotencyKey, nameof(idempotencyKey), 512);
        SemanticFingerprint = IdentityText.RequireSha256(semanticFingerprint, nameof(semanticFingerprint));
        DelegationId = delegationId;
        WorkflowRun = workflowRun;
        StructuralNode = structuralNode;
        NodeGeneration = nodeGeneration;
    }

    /// <summary>
    /// Gets the DelegationId value.
    /// </summary>
    public DelegationId DelegationId { get; }
    /// <summary>
    /// Gets the WorkflowRun value.
    /// </summary>
    public WorkflowRunExecutionReference WorkflowRun { get; }
    /// <summary>
    /// Gets the StructuralNode value.
    /// </summary>
    public StructuralNodeReference StructuralNode { get; }
    /// <summary>
    /// Gets the NodeGeneration value.
    /// </summary>
    public NodeGenerationId NodeGeneration { get; }
    /// <summary>
    /// Gets the ExecutionAttemptId value.
    /// </summary>
    public string ExecutionAttemptId { get; }
    /// <summary>
    /// Gets the IdempotencyKey value.
    /// </summary>
    public string IdempotencyKey { get; }
    /// <summary>
    /// Gets the SemanticFingerprint value.
    /// </summary>
    public string SemanticFingerprint { get; }

    /// <summary>
    /// Validates this contract value and throws when an invariant is violated.
    /// </summary>
    public void Validate()
    {
        ArtifactContracts.RequireDelegation(DelegationId, nameof(DelegationId));
        WorkflowRun.Validate();
        StructuralNode.Validate();
        NodeGeneration.Validate();
        IdentityText.Require(ExecutionAttemptId, nameof(ExecutionAttemptId), 512);
        IdentityText.Require(IdempotencyKey, nameof(IdempotencyKey), 512);
        IdentityText.RequireSha256(SemanticFingerprint, nameof(SemanticFingerprint));
    }
}

/// <summary>Bounded start hints. Providers may ignore unsupported hints.</summary>
public sealed record ExternalOperationBudgetHint
{
    /// <summary>
    /// Initializes a new instance of the ExternalOperationBudgetHint type.
    /// </summary>
    public ExternalOperationBudgetHint(int? maximumTokens = null, TimeSpan? maximumDuration = null)
    {
        if (maximumTokens is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumTokens));
        }

        if (maximumDuration is not null && maximumDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDuration));
        }

        MaximumTokens = maximumTokens;
        MaximumDuration = maximumDuration;
    }

    /// <summary>
    /// Gets the MaximumTokens value.
    /// </summary>
    public int? MaximumTokens { get; }
    /// <summary>
    /// Gets the MaximumDuration value.
    /// </summary>
    public TimeSpan? MaximumDuration { get; }
}

/// <summary>
/// Provider-neutral request. Inputs are immutable artifact references; raw
/// prompts, transcripts, credentials, and ambient paths are not part of this
/// contract.
/// </summary>
public sealed record ExternalOperationStartRequest
{
    /// <summary>
    /// Initializes a new instance of the ExternalOperationStartRequest type.
    /// </summary>
    public ExternalOperationStartRequest(
        ExternalOperationStartIdentity identity,
        ExternalOperationCorrelation correlation,
        string capability,
        IReadOnlyList<DelegationArtifactReference> inputArtifacts,
        ExternalOperationBudgetHint? budget = null,
        DateTimeOffset? deadline = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(correlation);
        identity.Validate();
        correlation.Validate();
        ExternalOperationContracts.RequireMatchingIdentity(identity, correlation);
        Capability = ArtifactContracts.Version(capability, nameof(capability));
        ArgumentNullException.ThrowIfNull(inputArtifacts);
        if (inputArtifacts.Count > ArtifactContracts.MaximumCollectionItems)
        {
            throw new ArgumentException(
                $"An external operation cannot contain more than {ArtifactContracts.MaximumCollectionItems} input artifacts.",
                nameof(inputArtifacts));
        }

        var snapshot = inputArtifacts.ToArray();
        var identities = new HashSet<(string Provider, string Repository, string ArtifactId)>();
        foreach (var artifact in snapshot)
        {
            ArgumentNullException.ThrowIfNull(artifact);
            ArtifactContracts.ValidateArtifact(artifact, identity.DelegationId);
            if (!identities.Add((artifact.Provider, artifact.Repository, artifact.ArtifactId)))
            {
                throw new ArgumentException("Input artifacts cannot contain duplicate identities.", nameof(inputArtifacts));
            }
        }

        Identity = identity;
        Correlation = correlation;
        InputArtifacts = Array.AsReadOnly(snapshot);
        Budget = budget;
        Deadline = deadline;
        SemanticInput = new ExternalOperationSemanticInputEnvelope(
            identity.DelegationId,
            correlation.Agent,
            Capability,
            InputArtifacts,
            Budget,
            Deadline);
    }

    /// <summary>
    /// Gets the Identity value.
    /// </summary>
    public ExternalOperationStartIdentity Identity { get; }
    /// <summary>
    /// Gets the Correlation value.
    /// </summary>
    public ExternalOperationCorrelation Correlation { get; }
    /// <summary>
    /// Gets the Capability value.
    /// </summary>
    public string Capability { get; }
    /// <summary>
    /// Gets the InputArtifacts value.
    /// </summary>
    public IReadOnlyList<DelegationArtifactReference> InputArtifacts { get; }
    /// <summary>
    /// Gets the Budget value.
    /// </summary>
    public ExternalOperationBudgetHint? Budget { get; }
    /// <summary>
    /// Gets the Deadline value.
    /// </summary>
    public DateTimeOffset? Deadline { get; }
    /// <summary>
    /// Exact semantic envelope whose canonical SHA-256 is carried by
    /// <see cref="ExternalOperationStartIdentity.SemanticFingerprint"/>. The
    /// envelope intentionally excludes transport details and is exposed so the
    /// Siming adapter can verify it without Qingniao duplicating canonicalization.
    /// </summary>
    public ExternalOperationSemanticInputEnvelope SemanticInput { get; }

    /// <summary>
    /// Verifies the opaque semantic fingerprint through the Siming adapter
    /// seam. Hosts must call this successfully before invoking StartAsync.
    /// </summary>
    public void VerifySemanticFingerprint(IExternalOperationSemanticFingerprintVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        if (!verifier.Matches(Identity, SemanticInput))
        {
            throw new InvalidOperationException(
                "The start semantic fingerprint does not match the exact semantic input envelope.");
        }
    }
}

/// <summary>
/// Versioned semantic inputs for an external start. Siming owns the canonical
/// serialization and hashing contract; Qingniao owns this bounded input shape.
/// </summary>
public sealed record ExternalOperationSemanticInputEnvelope
{
    /// <summary>
    /// Provides the CurrentVersion contract constant.
    /// </summary>
    public const string CurrentVersion = "external-start-semantics-v1";

    /// <summary>
    /// Initializes a new instance of the ExternalOperationSemanticInputEnvelope type.
    /// </summary>
    public ExternalOperationSemanticInputEnvelope(
        DelegationId delegationId,
        ExternalAgentReference agent,
        string capability,
        IReadOnlyList<DelegationArtifactReference> inputArtifacts,
        ExternalOperationBudgetHint? budget,
        DateTimeOffset? deadline)
    {
        ArtifactContracts.RequireDelegation(delegationId, nameof(delegationId));
        ArgumentNullException.ThrowIfNull(agent);
        agent.Validate();
        SchemaVersion = ArtifactContracts.Version(CurrentVersion, nameof(SchemaVersion));
        Capability = ArtifactContracts.Version(capability, nameof(capability));
        ArgumentNullException.ThrowIfNull(inputArtifacts);
        if (inputArtifacts.Count > ArtifactContracts.MaximumCollectionItems)
        {
            throw new ArgumentException("A semantic input envelope contains too many input artifacts.", nameof(inputArtifacts));
        }

        var snapshot = inputArtifacts.ToArray();
        var identities = new HashSet<(string Provider, string Repository, string ArtifactId)>();
        foreach (var artifact in snapshot)
        {
            ArgumentNullException.ThrowIfNull(artifact);
            ArtifactContracts.ValidateArtifact(artifact, delegationId);
            if (!identities.Add((artifact.Provider, artifact.Repository, artifact.ArtifactId)))
            {
                throw new ArgumentException(
                    "A semantic input envelope cannot contain duplicate artifact identities.",
                    nameof(inputArtifacts));
            }
        }

        DelegationId = delegationId;
        Agent = agent;
        InputArtifacts = Array.AsReadOnly(snapshot);
        Budget = budget;
        Deadline = deadline;
    }

    /// <summary>
    /// Gets the SchemaVersion value.
    /// </summary>
    public string SchemaVersion { get; }
    /// <summary>
    /// Gets the DelegationId value.
    /// </summary>
    public DelegationId DelegationId { get; }
    /// <summary>
    /// Gets the Agent value.
    /// </summary>
    public ExternalAgentReference Agent { get; }
    /// <summary>
    /// Gets the Capability value.
    /// </summary>
    public string Capability { get; }
    /// <summary>
    /// Gets the InputArtifacts value.
    /// </summary>
    public IReadOnlyList<DelegationArtifactReference> InputArtifacts { get; }
    /// <summary>
    /// Gets the Budget value.
    /// </summary>
    public ExternalOperationBudgetHint? Budget { get; }
    /// <summary>
    /// Gets the Deadline value.
    /// </summary>
    public DateTimeOffset? Deadline { get; }
}

/// <summary>
/// Verification seam for the Siming canonical-fingerprint adapter. Qingniao
/// deliberately does not implement canonical JSON or hash serialization here.
/// </summary>
public interface IExternalOperationSemanticFingerprintVerifier
{
    /// <summary>
    /// Performs the Matches contract operation.
    /// </summary>
    bool Matches(ExternalOperationStartIdentity identity, ExternalOperationSemanticInputEnvelope semanticInput);
}

/// <summary>Provider-issued reconnectable operation handle.</summary>
public sealed record ExternalOperationHandle
{
    /// <summary>
    /// Initializes a new instance of the ExternalOperationHandle type.
    /// </summary>
    public ExternalOperationHandle(
        string provider,
        string value,
        string protocolVersion,
        ExternalOperationCorrelation correlation)
    {
        Provider = IdentityText.Require(provider, nameof(provider), 128);
        Value = IdentityText.Require(value, nameof(value), 4_096);
        ProtocolVersion = ArtifactContracts.Version(protocolVersion, nameof(protocolVersion));
        ArgumentNullException.ThrowIfNull(correlation);
        correlation.Validate();
        correlation.EnsureTaskCaptured();
        if (!string.Equals(Provider, correlation.Agent.Provider, StringComparison.Ordinal))
        {
            throw new ArgumentException("An external handle provider must match the correlated agent provider.", nameof(provider));
        }

        if (!string.Equals(Provider, correlation.Task!.Provider, StringComparison.Ordinal))
        {
            throw new ArgumentException("An external handle provider must match the captured task provider.", nameof(provider));
        }

        if (!string.Equals(ProtocolVersion, correlation.Agent.ProtocolVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException("An external handle protocol version must match the correlated agent protocol.", nameof(protocolVersion));
        }
        Correlation = correlation;
    }

    /// <summary>
    /// Gets the Provider value.
    /// </summary>
    public string Provider { get; }
    /// <summary>
    /// Gets the Value value.
    /// </summary>
    public string Value { get; }
    /// <summary>
    /// Gets the ProtocolVersion value.
    /// </summary>
    public string ProtocolVersion { get; }
    /// <summary>
    /// Gets the Correlation value.
    /// </summary>
    public ExternalOperationCorrelation Correlation { get; }

    /// <summary>
    /// Validates this contract value and throws when an invariant is violated.
    /// </summary>
    public void Validate()
    {
        IdentityText.Require(Provider, nameof(Provider), 128);
        IdentityText.Require(Value, nameof(Value), 4_096);
        ArtifactContracts.Version(ProtocolVersion, nameof(ProtocolVersion));
        Correlation.Validate();
        Correlation.EnsureTaskCaptured();
        if (!string.Equals(Provider, Correlation.Agent.Provider, StringComparison.Ordinal)
            || !string.Equals(Provider, Correlation.Task!.Provider, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("An external handle provider must match its agent and task providers.");
        }

        if (!string.Equals(ProtocolVersion, Correlation.Agent.ProtocolVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("An external handle protocol version must match its agent protocol.");
        }
    }

    /// <summary>
    /// Performs the ToProviderAttemptReference contract operation.
    /// </summary>
    public ProviderExecutionAttemptReference ToProviderAttemptReference() =>
        new(Provider, Correlation.ExecutionAttemptId, Value);
}

/// <summary>Durable receipt of the earliest provider handle capture.</summary>
public sealed record ExternalOperationHandleCapture
{
    /// <summary>
    /// Initializes a new instance of the ExternalOperationHandleCapture type.
    /// </summary>
    public ExternalOperationHandleCapture(ExternalOperationHandle handle, DateTimeOffset capturedAt)
    {
        ArgumentNullException.ThrowIfNull(handle);
        handle.Validate();
        if (capturedAt == default)
        {
            throw new ArgumentException("A handle capture must have a timestamp.", nameof(capturedAt));
        }

        Handle = handle;
        CapturedAt = capturedAt;
    }

    /// <summary>
    /// Gets the Handle value.
    /// </summary>
    public ExternalOperationHandle Handle { get; }
    /// <summary>
    /// Gets the CapturedAt value.
    /// </summary>
    public DateTimeOffset CapturedAt { get; }
}

/// <summary>
/// Durable sink invoked as soon as a provider reveals a handle. Implementations
/// must persist it atomically and treat exact replays as idempotent.
/// </summary>
public interface IExternalOperationHandleCaptureSink
{
    /// <summary>
    /// Performs the CaptureAsync contract operation.
    /// </summary>
    ValueTask CaptureAsync(ExternalOperationHandleCapture capture, CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents the ExternalOperationStartDisposition contract and its invariants.
/// </summary>
public enum ExternalOperationStartDisposition
{
    /// <summary>
    /// Identifies the Created enum value.
    /// </summary>
    Created = 0,
    /// <summary>
    /// Identifies the Existing enum value.
    /// </summary>
    Existing = 1,
}

/// <summary>
/// Represents the ExternalOperationStartReceipt contract and its invariants.
/// </summary>
public sealed record ExternalOperationStartReceipt
{
    /// <summary>
    /// Initializes a new instance of the ExternalOperationStartReceipt type.
    /// </summary>
    public ExternalOperationStartReceipt(
        ExternalOperationStartIdentity identity,
        ExternalOperationHandle handle,
        ExternalOperationStartDisposition disposition,
        ExternalOperationState state,
        DateTimeOffset acceptedAt)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(handle);
        identity.Validate();
        handle.Validate();
        ExternalOperationContracts.RequireMatchingIdentity(identity, handle.Correlation);
        ExternalOperationContracts.RequireKnownState(state);
        if (acceptedAt == default)
        {
            throw new ArgumentException("A start receipt must have an acceptance timestamp.", nameof(acceptedAt));
        }

        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition));
        }

        Identity = identity;
        Handle = handle;
        Disposition = disposition;
        State = state;
        AcceptedAt = acceptedAt;
    }

    /// <summary>
    /// Gets the Identity value.
    /// </summary>
    public ExternalOperationStartIdentity Identity { get; }
    /// <summary>
    /// Gets the Handle value.
    /// </summary>
    public ExternalOperationHandle Handle { get; }
    /// <summary>
    /// Gets the Disposition value.
    /// </summary>
    public ExternalOperationStartDisposition Disposition { get; }
    /// <summary>
    /// Gets the State value.
    /// </summary>
    public ExternalOperationState State { get; }
    /// <summary>
    /// Gets the AcceptedAt value.
    /// </summary>
    public DateTimeOffset AcceptedAt { get; }
}

/// <summary>
/// Represents the ExternalOperationState contract and its invariants.
/// </summary>
public enum ExternalOperationState
{
    /// <summary>
    /// Identifies the Accepted enum value.
    /// </summary>
    Accepted = 0,
    /// <summary>
    /// Identifies the Running enum value.
    /// </summary>
    Running = 1,
    /// <summary>
    /// Identifies the Waiting enum value.
    /// </summary>
    Waiting = 2,
    /// <summary>
    /// Identifies the Succeeded enum value.
    /// </summary>
    Succeeded = 3,
    /// <summary>
    /// Identifies the Failed enum value.
    /// </summary>
    Failed = 4,
    /// <summary>
    /// Identifies the CancellationRequested enum value.
    /// </summary>
    CancellationRequested = 5,
    /// <summary>
    /// Identifies the Cancelled enum value.
    /// </summary>
    Cancelled = 6,
    /// <summary>
    /// Identifies the TimedOut enum value.
    /// </summary>
    TimedOut = 7,
    /// <summary>
    /// Identifies the Rejected enum value.
    /// </summary>
    Rejected = 8,
    /// <summary>The provider response is ambiguous; the operation may still be running.</summary>
    Unknown = 9,
}

/// <summary>
/// Represents the ExternalOperationFailureKind contract and its invariants.
/// </summary>
public enum ExternalOperationFailureKind
{
    /// <summary>
    /// Identifies the Transport enum value.
    /// </summary>
    Transport = 0,
    /// <summary>
    /// Identifies the Remote enum value.
    /// </summary>
    Remote = 1,
    /// <summary>
    /// Identifies the Cancellation enum value.
    /// </summary>
    Cancellation = 2,
    /// <summary>
    /// Identifies the Timeout enum value.
    /// </summary>
    Timeout = 3,
    /// <summary>
    /// Identifies the Rejection enum value.
    /// </summary>
    Rejection = 4,
    /// <summary>
    /// Identifies the ResultValidation enum value.
    /// </summary>
    ResultValidation = 5,
}

/// <summary>Stable, policy-readable classification of an external failure.</summary>
public sealed record ExternalOperationFailure
{
    /// <summary>
    /// Initializes a new instance of the ExternalOperationFailure type.
    /// </summary>
    public ExternalOperationFailure(
        ExternalOperationFailureKind kind,
        string code,
        string summary,
        bool retryable,
        string? providerCode = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Kind = kind;
        Code = ArtifactContracts.Version(code, nameof(code));
        Summary = IdentityText.RequireProse(summary, nameof(summary), 8_192);
        ProviderCode = providerCode is null ? null : IdentityText.Require(providerCode, nameof(providerCode), 512);
        Retryable = retryable;
    }

    /// <summary>
    /// Gets the Kind value.
    /// </summary>
    public ExternalOperationFailureKind Kind { get; }
    /// <summary>
    /// Gets the Code value.
    /// </summary>
    public string Code { get; }
    /// <summary>
    /// Gets the Summary value.
    /// </summary>
    public string Summary { get; }
    /// <summary>
    /// Gets the Retryable value.
    /// </summary>
    public bool Retryable { get; }
    /// <summary>
    /// Gets the ProviderCode value.
    /// </summary>
    public string? ProviderCode { get; }
}

/// <summary>
/// Carries a validated provider failure across the external-operation adapter
/// boundary without exposing an adapter exception message or raw provider
/// payload to orchestration code.
/// </summary>
public sealed class ExternalOperationProviderException : Exception
{
    /// <summary>Initializes a provider exception from classified failure data.</summary>
    public ExternalOperationProviderException(ExternalOperationFailure failure)
        : base("The external operation provider reported a classified failure.")
    {
        Failure = failure ?? throw new ArgumentNullException(nameof(failure));
    }

    /// <summary>Gets the validated, policy-readable provider failure.</summary>
    public ExternalOperationFailure Failure { get; }
}

/// <summary>
/// Represents the ExternalOperationObservation contract and its invariants.
/// </summary>
public sealed record ExternalOperationObservation
{
    /// <summary>
    /// Initializes a new instance of the ExternalOperationObservation type.
    /// </summary>
    public ExternalOperationObservation(
        ExternalOperationHandle handle,
        long revision,
        ExternalOperationState state,
        DateTimeOffset observedAt,
        string? providerStatus = null,
        ExternalOperationFailure? failure = null,
        bool resultAvailable = false)
    {
        ArgumentNullException.ThrowIfNull(handle);
        handle.Validate();
        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        ExternalOperationContracts.RequireKnownState(state);
        if (observedAt == default)
        {
            throw new ArgumentException("An observation must have a timestamp.", nameof(observedAt));
        }

        if (providerStatus is not null)
        {
            IdentityText.Require(providerStatus, nameof(providerStatus), 512);
        }

        if (state == ExternalOperationState.Succeeded && failure is not null)
        {
            throw new ArgumentException("A successful observation cannot contain a failure.", nameof(failure));
        }

        if (failure is null && state is (ExternalOperationState.Failed
            or ExternalOperationState.TimedOut
            or ExternalOperationState.Rejected))
        {
            throw new ArgumentException("Failed, timed-out, and rejected observations require failure evidence.", nameof(failure));
        }

        if (failure is not null)
        {
            ExternalOperationContracts.RequireFailureMatchesState(state, failure);
        }

        if (resultAvailable && state is not (ExternalOperationState.Succeeded
            or ExternalOperationState.Failed
            or ExternalOperationState.Cancelled
            or ExternalOperationState.TimedOut
            or ExternalOperationState.Rejected))
        {
            throw new ArgumentException("A result cannot be available for a non-terminal observation.", nameof(resultAvailable));
        }

        Handle = handle;
        Revision = revision;
        State = state;
        ObservedAt = observedAt;
        ProviderStatus = providerStatus;
        Failure = failure;
        ResultAvailable = resultAvailable;
    }

    /// <summary>
    /// Gets the Handle value.
    /// </summary>
    public ExternalOperationHandle Handle { get; }
    /// <summary>
    /// Gets the Revision value.
    /// </summary>
    public long Revision { get; }
    /// <summary>
    /// Gets the State value.
    /// </summary>
    public ExternalOperationState State { get; }
    /// <summary>
    /// Gets the ObservedAt value.
    /// </summary>
    public DateTimeOffset ObservedAt { get; }
    /// <summary>
    /// Gets the ProviderStatus value.
    /// </summary>
    public string? ProviderStatus { get; }
    /// <summary>
    /// Gets the Failure value.
    /// </summary>
    public ExternalOperationFailure? Failure { get; }
    /// <summary>
    /// Gets the ResultAvailable value.
    /// </summary>
    public bool ResultAvailable { get; }
}

/// <summary>
/// Represents the ExternalOperationObservationRules contract and its invariants.
/// </summary>
public static class ExternalOperationObservationRules
{
    /// <summary>
    /// Performs the ValidateProgression contract operation.
    /// </summary>
    public static void ValidateProgression(ExternalOperationObservation previous, ExternalOperationObservation current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        previous.Handle.Validate();
        current.Handle.Validate();
        if (previous.Handle != current.Handle)
        {
            throw new InvalidOperationException("Observations must belong to the same external handle.");
        }

        if (current.Revision < previous.Revision)
        {
            throw new InvalidOperationException("An observation revision cannot move backwards.");
        }

        if (current.ObservedAt < previous.ObservedAt)
        {
            throw new InvalidOperationException("An observation timestamp cannot move backwards.");
        }

        if (ExternalOperationContracts.IsTerminal(previous.State)
            && (current.State != previous.State || current.Revision != previous.Revision))
        {
            throw new InvalidOperationException("A terminal external operation observation cannot be changed.");
        }

        if (current.Revision == previous.Revision && !Equals(previous, current))
        {
            throw new InvalidOperationException("A changed external observation requires a greater revision.");
        }

        if (current.Revision > previous.Revision
            && !ExternalOperationContracts.CanTransition(previous.State, current.State))
        {
            throw new InvalidOperationException(
                $"An external operation cannot transition from '{previous.State}' to '{current.State}'.");
        }
    }
}

/// <summary>
/// Represents the ExternalOperationResult contract and its invariants.
/// </summary>
#pragma warning disable RS0026, RS0027
public sealed record ExternalOperationResult
{
    /// <summary>Initializes a result with a provider-sealed candidate.</summary>
    public ExternalOperationResult(
        ExternalOperationHandle handle,
        ExternalOperationState state,
        DateTimeOffset completedAt,
        string summary,
        IReadOnlyList<DelegationArtifactReference> artifacts,
        CandidateRevisionReference candidate,
        ExternalOperationProvenanceSnapshot? provenance = null,
        ExternalOperationFailure? failure = null)
        : this(handle, state, completedAt, summary, artifacts, provenance, failure, candidate)
    {
    }

    /// <summary>Initializes a legacy result without candidate evidence.</summary>
    public ExternalOperationResult(
        ExternalOperationHandle handle,
        ExternalOperationState state,
        DateTimeOffset completedAt,
        string summary,
        IReadOnlyList<DelegationArtifactReference> artifacts,
        ExternalOperationProvenanceSnapshot? provenance = null,
        ExternalOperationFailure? failure = null)
        : this(handle, state, completedAt, summary, artifacts, provenance, failure, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the ExternalOperationResult type.
    /// </summary>
    public ExternalOperationResult(
        ExternalOperationHandle handle,
        ExternalOperationState state,
        DateTimeOffset completedAt,
        string summary,
        IReadOnlyList<DelegationArtifactReference> artifacts,
        ExternalOperationProvenanceSnapshot? provenance,
        ExternalOperationFailure? failure,
        CandidateRevisionReference? candidate)
    {
        ArgumentNullException.ThrowIfNull(handle);
        handle.Validate();
        if (!ExternalOperationContracts.IsTerminal(state))
        {
            throw new ArgumentException("An external result must use a terminal state.", nameof(state));
        }

        if (completedAt == default)
        {
            throw new ArgumentException("An external result must have a completion timestamp.", nameof(completedAt));
        }

        Summary = IdentityText.RequireProse(summary, nameof(summary), 8_192);
        ArgumentNullException.ThrowIfNull(artifacts);
        if (artifacts.Count > ArtifactContracts.MaximumCollectionItems)
        {
            throw new ArgumentException("An external result contains too many artifacts.", nameof(artifacts));
        }

        var snapshot = artifacts.ToArray();
        var identities = new HashSet<(string Provider, string Repository, string ArtifactId)>();
        foreach (var artifact in snapshot)
        {
            ArgumentNullException.ThrowIfNull(artifact);
            ArtifactContracts.ValidateArtifact(
                artifact,
                handle.Correlation.DelegationId,
                handle.Correlation.StructuralNode,
                handle.Correlation.NodeGeneration);
            if (!identities.Add((artifact.Provider, artifact.Repository, artifact.ArtifactId)))
            {
                throw new ArgumentException("An external result cannot contain duplicate artifacts.", nameof(artifacts));
            }
        }

        if (failure is null && state is (ExternalOperationState.Failed
            or ExternalOperationState.TimedOut
            or ExternalOperationState.Rejected))
        {
            throw new ArgumentException("Failed, timed-out, and rejected results require failure evidence.", nameof(failure));
        }

        if (failure is not null)
        {
            ExternalOperationContracts.RequireFailureMatchesState(state, failure);
        }

        if (state == ExternalOperationState.Succeeded && failure is not null)
        {
            throw new ArgumentException("A successful external result cannot contain a failure.", nameof(failure));
        }

        if (candidate is not null)
        {
            if (candidate.DelegationId != handle.Correlation.DelegationId
                || candidate.StructuralNode != handle.Correlation.StructuralNode
                || candidate.NodeGeneration != handle.Correlation.NodeGeneration)
            {
                throw new ArgumentException(
                    "A result candidate must belong to the exact external correlation.",
                    nameof(candidate));
            }

            // The candidate is a sealed reference to the result artifacts.  A
            // provider cannot attach a candidate for a different payload (or
            // silently manufacture one from a mutable path).
            if (!candidate.Artifacts.SequenceEqual(snapshot))
            {
                throw new ArgumentException(
                    "A result candidate must reference exactly the result artifacts.",
                    nameof(candidate));
            }
        }

        Handle = handle;
        State = state;
        CompletedAt = completedAt;
        Artifacts = Array.AsReadOnly(snapshot);
        Provenance = provenance;
        Failure = failure;
        Candidate = candidate;
    }

    /// <summary>
    /// Gets the Handle value.
    /// </summary>
    public ExternalOperationHandle Handle { get; }
    /// <summary>
    /// Gets the State value.
    /// </summary>
    public ExternalOperationState State { get; }
    /// <summary>
    /// Gets the CompletedAt value.
    /// </summary>
    public DateTimeOffset CompletedAt { get; }
    /// <summary>
    /// Gets the Summary value.
    /// </summary>
    public string Summary { get; }
    /// <summary>
    /// Gets the Artifacts value.
    /// </summary>
    public IReadOnlyList<DelegationArtifactReference> Artifacts { get; }
    /// <summary>
    /// Gets the Provenance value.
    /// </summary>
    public ExternalOperationProvenanceSnapshot? Provenance { get; }
    /// <summary>
    /// Gets the Failure value.
    /// </summary>
    public ExternalOperationFailure? Failure { get; }
    /// <summary>
    /// Gets the optional immutable candidate sealed by the external provider.
    /// </summary>
    public CandidateRevisionReference? Candidate { get; }
}

/// <summary>
/// Represents the ExternalOperationCancellationDisposition contract and its invariants.
/// </summary>
public enum ExternalOperationCancellationDisposition
{
    /// <summary>
    /// Identifies the Requested enum value.
    /// </summary>
    Requested = 0,
    /// <summary>
    /// Identifies the ConfirmedCancelled enum value.
    /// </summary>
    ConfirmedCancelled = 1,
    /// <summary>
    /// Identifies the AlreadyTerminal enum value.
    /// </summary>
    AlreadyTerminal = 2,
    /// <summary>
    /// Identifies the Rejected enum value.
    /// </summary>
    Rejected = 3,
    /// <summary>
    /// Identifies the Unknown enum value.
    /// </summary>
    Unknown = 4,
}

/// <summary>
/// Represents the ExternalOperationCancelRequest contract and its invariants.
/// </summary>
public sealed record ExternalOperationCancelRequest
{
    /// <summary>
    /// Initializes a new instance of the ExternalOperationCancelRequest type.
    /// </summary>
    public ExternalOperationCancelRequest(ExternalOperationHandle handle, string cancellationKey, string reason)
    {
        ArgumentNullException.ThrowIfNull(handle);
        handle.Validate();
        Handle = handle;
        CancellationKey = IdentityText.Require(cancellationKey, nameof(cancellationKey), 512);
        Reason = IdentityText.RequireProse(reason, nameof(reason), 4_096);
    }

    /// <summary>
    /// Gets the Handle value.
    /// </summary>
    public ExternalOperationHandle Handle { get; }
    /// <summary>
    /// Gets the CancellationKey value.
    /// </summary>
    public string CancellationKey { get; }
    /// <summary>
    /// Gets the Reason value.
    /// </summary>
    public string Reason { get; }
}

/// <summary>
/// Represents the ExternalOperationCancellationReceipt contract and its invariants.
/// </summary>
public sealed record ExternalOperationCancellationReceipt
{
    /// <summary>
    /// Initializes a new instance of the ExternalOperationCancellationReceipt type.
    /// </summary>
    public ExternalOperationCancellationReceipt(
        ExternalOperationHandle handle,
        string cancellationKey,
        ExternalOperationCancellationDisposition disposition,
        ExternalOperationState state,
        DateTimeOffset recordedAt,
        ExternalOperationFailure? failure = null)
    {
        ArgumentNullException.ThrowIfNull(handle);
        handle.Validate();
        CancellationKey = IdentityText.Require(cancellationKey, nameof(cancellationKey), 512);
        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition));
        }

        ExternalOperationContracts.RequireKnownState(state);
        if (recordedAt == default)
        {
            throw new ArgumentException("A cancellation receipt must have a timestamp.", nameof(recordedAt));
        }

        var stateMatchesDisposition = disposition switch
        {
            ExternalOperationCancellationDisposition.Requested => state == ExternalOperationState.CancellationRequested,
            ExternalOperationCancellationDisposition.ConfirmedCancelled => state == ExternalOperationState.Cancelled,
            ExternalOperationCancellationDisposition.Unknown => state == ExternalOperationState.Unknown,
            ExternalOperationCancellationDisposition.Rejected => !ExternalOperationContracts.IsTerminal(state),
            ExternalOperationCancellationDisposition.AlreadyTerminal => ExternalOperationContracts.IsTerminal(state),
            _ => false,
        };
        if (!stateMatchesDisposition)
        {
            throw new ArgumentException(
                $"Cancellation disposition '{disposition}' is inconsistent with state '{state}'.",
                nameof(state));
        }

        if (disposition is ExternalOperationCancellationDisposition.Requested
            or ExternalOperationCancellationDisposition.ConfirmedCancelled
            or ExternalOperationCancellationDisposition.AlreadyTerminal)
        {
            if (failure is not null)
            {
                throw new ArgumentException("This cancellation disposition cannot carry failure evidence.", nameof(failure));
            }
        }
        else if (failure is not null && failure.Kind != ExternalOperationFailureKind.Cancellation)
        {
            throw new ArgumentException("Cancellation receipts must classify failures as cancellation failures.", nameof(failure));
        }

        if (disposition is (ExternalOperationCancellationDisposition.Rejected
            or ExternalOperationCancellationDisposition.Unknown)
            && failure is null)
        {
            throw new ArgumentException("A rejected or unknown cancellation must carry cancellation failure evidence.", nameof(failure));
        }

        Handle = handle;
        Disposition = disposition;
        State = state;
        RecordedAt = recordedAt;
        Failure = failure;
    }

    /// <summary>
    /// Gets the Handle value.
    /// </summary>
    public ExternalOperationHandle Handle { get; }
    /// <summary>
    /// Gets the CancellationKey value.
    /// </summary>
    public string CancellationKey { get; }
    /// <summary>
    /// Gets the Disposition value.
    /// </summary>
    public ExternalOperationCancellationDisposition Disposition { get; }
    /// <summary>
    /// Gets the State value.
    /// </summary>
    public ExternalOperationState State { get; }
    /// <summary>
    /// Gets the RecordedAt value.
    /// </summary>
    public DateTimeOffset RecordedAt { get; }
    /// <summary>
    /// Gets the Failure value.
    /// </summary>
    public ExternalOperationFailure? Failure { get; }
}

/// <summary>
/// Resumes an accepted operation using its durable handle. Corrections are
/// artifact references, never an unbounded prompt or transcript.
/// </summary>
public sealed record ExternalOperationResumeRequest
{
    /// <summary>
    /// Initializes a new instance of the ExternalOperationResumeRequest type.
    /// </summary>
    public ExternalOperationResumeRequest(
        ExternalOperationHandle handle,
        string resumeKey,
        IReadOnlyList<DelegationArtifactReference>? correctionArtifacts = null,
        string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(handle);
        handle.Validate();
        Handle = handle;
        ResumeKey = IdentityText.Require(resumeKey, nameof(resumeKey), 512);
        var corrections = correctionArtifacts ?? Array.Empty<DelegationArtifactReference>();
        if (corrections.Count > ArtifactContracts.MaximumCollectionItems)
        {
            throw new ArgumentException("A resume request contains too many correction artifacts.", nameof(correctionArtifacts));
        }

        var snapshot = corrections.ToArray();
        var identities = new HashSet<(string Provider, string Repository, string ArtifactId)>();
        foreach (var artifact in snapshot)
        {
            ArgumentNullException.ThrowIfNull(artifact);
            ArtifactContracts.ValidateArtifact(artifact, handle.Correlation.DelegationId);
            if (!identities.Add((artifact.Provider, artifact.Repository, artifact.ArtifactId)))
            {
                throw new ArgumentException("Correction artifacts cannot contain duplicate identities.", nameof(correctionArtifacts));
            }
        }

        CorrectionArtifacts = Array.AsReadOnly(snapshot);
        Reason = reason is null ? null : IdentityText.RequireProse(reason, nameof(reason), 4_096);
    }

    /// <summary>
    /// Gets the Handle value.
    /// </summary>
    public ExternalOperationHandle Handle { get; }
    /// <summary>
    /// Gets the ResumeKey value.
    /// </summary>
    public string ResumeKey { get; }
    /// <summary>
    /// Gets the CorrectionArtifacts value.
    /// </summary>
    public IReadOnlyList<DelegationArtifactReference> CorrectionArtifacts { get; }
    /// <summary>
    /// Gets the Reason value.
    /// </summary>
    public string? Reason { get; }
}

/// <summary>Durable, idempotency-keyed receipt for one resume request.</summary>
public sealed record ExternalOperationResumeReceipt
{
    /// <summary>
    /// Initializes a new instance of the ExternalOperationResumeReceipt type.
    /// </summary>
    public ExternalOperationResumeReceipt(
        ExternalOperationHandle previousHandle,
        string resumeKey,
        ExternalOperationHandle handle,
        ExternalOperationStartDisposition disposition,
        ExternalOperationState state,
        DateTimeOffset acceptedAt)
    {
        ArgumentNullException.ThrowIfNull(previousHandle);
        ArgumentNullException.ThrowIfNull(handle);
        previousHandle.Validate();
        handle.Validate();
        ResumeKey = IdentityText.Require(resumeKey, nameof(resumeKey), 512);
        if (previousHandle.Correlation != handle.Correlation)
        {
            throw new ArgumentException("A resume receipt must preserve the exact external operation correlation.", nameof(handle));
        }

        ExternalOperationContracts.RequireKnownState(state);
        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition));
        }

        if (acceptedAt == default)
        {
            throw new ArgumentException("A resume receipt must have an acceptance timestamp.", nameof(acceptedAt));
        }

        PreviousHandle = previousHandle;
        Handle = handle;
        Disposition = disposition;
        State = state;
        AcceptedAt = acceptedAt;
    }

    /// <summary>
    /// Gets the PreviousHandle value.
    /// </summary>
    public ExternalOperationHandle PreviousHandle { get; }
    /// <summary>
    /// Gets the ResumeKey value.
    /// </summary>
    public string ResumeKey { get; }
    /// <summary>
    /// Gets the Handle value.
    /// </summary>
    public ExternalOperationHandle Handle { get; }
    /// <summary>
    /// Gets the Disposition value.
    /// </summary>
    public ExternalOperationStartDisposition Disposition { get; }
    /// <summary>
    /// Gets the State value.
    /// </summary>
    public ExternalOperationState State { get; }
    /// <summary>
    /// Gets the AcceptedAt value.
    /// </summary>
    public DateTimeOffset AcceptedAt { get; }
}

/// <summary>Provider-neutral durable external-operation adapter seam.</summary>
public interface IExternalOperationProvider
{
    /// <remarks>
    /// Hosts MUST call <see cref="ExternalOperationStartRequest.VerifySemanticFingerprint"/>
    /// with their Siming-backed verifier before invoking this method.
    /// The provider must invoke <paramref name="handleSink"/> immediately
    /// after learning the task handle, before waiting for final acceptance or
    /// result data. Losing the return value is therefore recoverable.
    /// </remarks>
    ValueTask<ExternalOperationStartReceipt> StartAsync(
        ExternalOperationStartRequest request,
        IExternalOperationHandleCaptureSink handleSink,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs the ObserveAsync contract operation.
    /// </summary>
    ValueTask<ExternalOperationObservation> ObserveAsync(
        ExternalOperationHandle handle,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs the GetResultAsync contract operation.
    /// </summary>
    ValueTask<ExternalOperationResult> GetResultAsync(
        ExternalOperationHandle handle,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs the CancelAsync contract operation.
    /// </summary>
    ValueTask<ExternalOperationCancellationReceipt> CancelAsync(
        ExternalOperationCancelRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs the ResumeAsync contract operation.
    /// </summary>
    ValueTask<ExternalOperationResumeReceipt> ResumeAsync(
        ExternalOperationResumeRequest request,
        IExternalOperationHandleCaptureSink handleSink,
        CancellationToken cancellationToken = default);
}

/// <summary>Versioned model identity captured at invocation time.</summary>
public sealed record ModelProvenanceSnapshot
{
    /// <summary>
    /// Provides the CurrentSchemaVersion contract constant.
    /// </summary>
    public const string CurrentSchemaVersion = "v1";

    /// <summary>
    /// Initializes a new instance of the ModelProvenanceSnapshot type.
    /// </summary>
    public ModelProvenanceSnapshot(
        string provider,
        string model,
        string? modelRevision = null,
        string? profile = null,
        string schemaVersion = CurrentSchemaVersion)
    {
        SchemaVersion = ArtifactContracts.Version(schemaVersion, nameof(schemaVersion));
        Provider = IdentityText.Require(provider, nameof(provider), 256);
        Model = IdentityText.Require(model, nameof(model), 512);
        ModelRevision = modelRevision is null ? null : IdentityText.Require(modelRevision, nameof(modelRevision), 512);
        Profile = profile is null ? null : IdentityText.Require(profile, nameof(profile), 512);
    }

    /// <summary>
    /// Gets the SchemaVersion value.
    /// </summary>
    public string SchemaVersion { get; }
    /// <summary>
    /// Gets the Provider value.
    /// </summary>
    public string Provider { get; }
    /// <summary>
    /// Gets the Model value.
    /// </summary>
    public string Model { get; }
    /// <summary>
    /// Gets the ModelRevision value.
    /// </summary>
    public string? ModelRevision { get; }
    /// <summary>
    /// Gets the Profile value.
    /// </summary>
    public string? Profile { get; }
}

/// <summary>
/// Represents the ToolProvenance contract and its invariants.
/// </summary>
public sealed record ToolProvenance
{
    /// <summary>
    /// Initializes a new instance of the ToolProvenance type.
    /// </summary>
    public ToolProvenance(string name, string? version = null, ArtifactContentIdentity? contentIdentity = null)
    {
        Name = IdentityText.Require(name, nameof(name), 512);
        Version = version is null ? null : IdentityText.Require(version, nameof(version), 256);
        contentIdentity?.Validate();
        ContentIdentity = contentIdentity;
    }

    /// <summary>
    /// Gets the Name value.
    /// </summary>
    public string Name { get; }
    /// <summary>
    /// Gets the Version value.
    /// </summary>
    public string? Version { get; }
    /// <summary>
    /// Gets the ContentIdentity value.
    /// </summary>
    public ArtifactContentIdentity? ContentIdentity { get; }
}

/// <summary>Versioned immutable tool capability snapshot.</summary>
public sealed record ToolProvenanceSnapshot
{
    /// <summary>
    /// Provides the CurrentSchemaVersion contract constant.
    /// </summary>
    public const string CurrentSchemaVersion = "v1";

    /// <summary>
    /// Initializes a new instance of the ToolProvenanceSnapshot type.
    /// </summary>
    public ToolProvenanceSnapshot(
        IReadOnlyList<ToolProvenance> tools,
        string schemaVersion = CurrentSchemaVersion)
    {
        SchemaVersion = ArtifactContracts.Version(schemaVersion, nameof(schemaVersion));
        ArgumentNullException.ThrowIfNull(tools);
        if (tools.Count > 64)
        {
            throw new ArgumentException("A tool provenance snapshot cannot contain more than 64 tools.", nameof(tools));
        }

        var snapshot = tools.ToArray();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in snapshot)
        {
            ArgumentNullException.ThrowIfNull(tool);
            if (!names.Add(tool.Name))
            {
                throw new ArgumentException("A tool provenance snapshot cannot contain duplicate tools.", nameof(tools));
            }
        }

        Tools = Array.AsReadOnly(snapshot);
    }

    /// <summary>
    /// Gets the SchemaVersion value.
    /// </summary>
    public string SchemaVersion { get; }
    /// <summary>
    /// Gets the Tools value.
    /// </summary>
    public IReadOnlyList<ToolProvenance> Tools { get; }
}

/// <summary>
/// Versioned usage measurements; values are deliberately strings to preserve
/// provider units. Values are not treated as a secret scanner: adapters and
/// redaction/retention policy remain authoritative, while known dangerous key
/// names are rejected as a bounded guardrail.
/// </summary>
public sealed record UsageProvenanceSnapshot
{
    /// <summary>
    /// Provides the CurrentSchemaVersion contract constant.
    /// </summary>
    public const string CurrentSchemaVersion = "v1";

    /// <summary>
    /// Initializes a new instance of the UsageProvenanceSnapshot type.
    /// </summary>
    public UsageProvenanceSnapshot(
        IReadOnlyDictionary<string, string>? measurements = null,
        string schemaVersion = CurrentSchemaVersion)
    {
        SchemaVersion = ArtifactContracts.Version(schemaVersion, nameof(schemaVersion));
        Measurements = ExternalOperationContracts.SafeProperties(measurements, nameof(measurements));
    }

    /// <summary>
    /// Gets the SchemaVersion value.
    /// </summary>
    public string SchemaVersion { get; }
    /// <summary>
    /// Gets the Measurements value.
    /// </summary>
    public IReadOnlyDictionary<string, string> Measurements { get; }
}

/// <summary>Atomic versioned provenance envelope attached to an operation result.</summary>
public sealed record ExternalOperationProvenanceSnapshot
{
    /// <summary>
    /// Provides the CurrentSchemaVersion contract constant.
    /// </summary>
    public const string CurrentSchemaVersion = "v1";

    /// <summary>
    /// Initializes a new instance of the ExternalOperationProvenanceSnapshot type.
    /// </summary>
    public ExternalOperationProvenanceSnapshot(
        ModelProvenanceSnapshot? model,
        ToolProvenanceSnapshot tools,
        UsageProvenanceSnapshot usage,
        string schemaVersion = CurrentSchemaVersion)
    {
        SchemaVersion = ArtifactContracts.Version(schemaVersion, nameof(schemaVersion));
        Tools = tools ?? throw new ArgumentNullException(nameof(tools));
        Usage = usage ?? throw new ArgumentNullException(nameof(usage));
        Model = model;
    }

    /// <summary>
    /// Gets the SchemaVersion value.
    /// </summary>
    public string SchemaVersion { get; }
    /// <summary>
    /// Gets the Model value.
    /// </summary>
    public ModelProvenanceSnapshot? Model { get; }
    /// <summary>
    /// Gets the Tools value.
    /// </summary>
    public ToolProvenanceSnapshot Tools { get; }
    /// <summary>
    /// Gets the Usage value.
    /// </summary>
    public UsageProvenanceSnapshot Usage { get; }
}

internal static class ExternalOperationContracts
{
    /// <summary>
    /// Performs the IsTerminal contract operation.
    /// </summary>
    public static bool IsTerminal(ExternalOperationState state) => state is
        ExternalOperationState.Succeeded
        or ExternalOperationState.Failed
        or ExternalOperationState.Cancelled
        or ExternalOperationState.TimedOut
        or ExternalOperationState.Rejected;

    /// <summary>
    /// Performs the CanTransition contract operation.
    /// </summary>
    public static bool CanTransition(ExternalOperationState current, ExternalOperationState next)
    {
        RequireKnownState(current);
        RequireKnownState(next);
        if (current == next)
        {
            return true;
        }

        if (IsTerminal(current))
        {
            return false;
        }

        return current switch
        {
            ExternalOperationState.Accepted => next is ExternalOperationState.Running
                or ExternalOperationState.Waiting
                or ExternalOperationState.Succeeded
                or ExternalOperationState.Failed
                or ExternalOperationState.Cancelled
                or ExternalOperationState.TimedOut
                or ExternalOperationState.CancellationRequested
                or ExternalOperationState.Unknown
                or ExternalOperationState.Rejected,
            ExternalOperationState.Running => next is ExternalOperationState.Waiting
                or ExternalOperationState.Succeeded
                or ExternalOperationState.Failed
                or ExternalOperationState.CancellationRequested
                or ExternalOperationState.TimedOut
                or ExternalOperationState.Rejected
                or ExternalOperationState.Unknown,
            ExternalOperationState.Waiting => next is ExternalOperationState.Running
                or ExternalOperationState.Succeeded
                or ExternalOperationState.Failed
                or ExternalOperationState.CancellationRequested
                or ExternalOperationState.TimedOut
                or ExternalOperationState.Rejected
                or ExternalOperationState.Unknown,
            ExternalOperationState.CancellationRequested => next is ExternalOperationState.Running
                or ExternalOperationState.Waiting
                or ExternalOperationState.Succeeded
                or ExternalOperationState.Failed
                or ExternalOperationState.Cancelled
                or ExternalOperationState.TimedOut
                or ExternalOperationState.Rejected
                or ExternalOperationState.Unknown,
            ExternalOperationState.Unknown => next != ExternalOperationState.Accepted,
            _ => false,
        };
    }

    /// <summary>
    /// Performs the RequireKnownState contract operation.
    /// </summary>
    public static void RequireKnownState(ExternalOperationState state)
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown external operation state.");
        }
    }

    /// <summary>
    /// Performs the RequireMatchingIdentity contract operation.
    /// </summary>
    public static void RequireMatchingIdentity(
        ExternalOperationStartIdentity identity,
        ExternalOperationCorrelation correlation)
    {
        if (identity.DelegationId != correlation.DelegationId
            || identity.WorkflowRun != correlation.WorkflowRun
            || identity.StructuralNode != correlation.StructuralNode
            || identity.NodeGeneration != correlation.NodeGeneration
            || !string.Equals(identity.ExecutionAttemptId, correlation.ExecutionAttemptId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Start identity and operation correlation must identify the same attempt.");
        }
    }

    /// <summary>
    /// Performs the RequireFailureMatchesState contract operation.
    /// </summary>
    public static void RequireFailureMatchesState(ExternalOperationState state, ExternalOperationFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var valid = state switch
        {
            ExternalOperationState.Unknown => failure.Kind == ExternalOperationFailureKind.Transport,
            ExternalOperationState.Failed => failure.Kind is ExternalOperationFailureKind.Remote
                or ExternalOperationFailureKind.ResultValidation,
            ExternalOperationState.TimedOut => failure.Kind == ExternalOperationFailureKind.Timeout,
            ExternalOperationState.Rejected => failure.Kind == ExternalOperationFailureKind.Rejection,
            _ => false,
        };

        if (!valid)
        {
            throw new ArgumentException($"Failure kind '{failure.Kind}' is not valid for state '{state}'.", nameof(failure));
        }
    }

    /// <summary>
    /// Performs the SafeProperties contract operation.
    /// </summary>
    public static IReadOnlyDictionary<string, string> SafeProperties(
        IReadOnlyDictionary<string, string>? values,
        string parameterName)
    {
        if (values is null || values.Count == 0)
        {
            return new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(StringComparer.Ordinal));
        }

        if (values.Count > 64)
        {
            throw new ArgumentException("A provenance map cannot contain more than 64 values.", parameterName);
        }

        var copy = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in values)
        {
            var key = ArtifactContracts.Version(pair.Key, parameterName);
            var value = IdentityText.RequireProse(pair.Value, parameterName, 1_024);
            if (ContainsDangerousKey(key))
            {
                throw new ArgumentException(
                    "Provenance keys cannot name credentials, authorization data, or transcripts. Adapters and redaction policy remain authoritative for values.",
                    parameterName);
            }

            if (!copy.TryAdd(key, value))
            {
                throw new ArgumentException("A provenance map cannot contain duplicate keys.", parameterName);
            }
        }

        return new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(copy);
    }

    private static bool ContainsDangerousKey(string value)
    {
        var dangerousSegments = new HashSet<string>(StringComparer.Ordinal)
        {
            "secret",
            "secrets",
            "password",
            "passwords",
            "credential",
            "credentials",
            "authorization",
            "transcript",
            "access_token",
            "refresh_token",
        };

        var normalized = value.Replace('.', '_').Replace('-', '_');
        return normalized.Contains("access_token", StringComparison.Ordinal)
            || normalized.Contains("refresh_token", StringComparison.Ordinal)
            || dangerousSegments.Contains(normalized)
            || value.Split(['.', '-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => dangerousSegments.Contains(segment));
    }
}
