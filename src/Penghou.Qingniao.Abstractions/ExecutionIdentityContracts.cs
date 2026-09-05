namespace Penghou.Qingniao;

/// <summary>Identifies the kind of plan revision selected for a delegation.</summary>
public enum WorkflowPlanReferenceKind
{
    /// <summary>
    /// Identifies the BuiltInPreset enum value.
    /// </summary>
    BuiltInPreset = 0,
    /// <summary>
    /// Identifies the FuwenDefinition enum value.
    /// </summary>
    FuwenDefinition = 1,
}

/// <summary>
/// A workflow plan selection identity. The value does not prove authorization,
/// verification, or binding; the host policy/resolver must establish those
/// properties before execution. This is not a Fuwen runtime type.
/// </summary>
public sealed record WorkflowPlanRevisionReference
{
    /// <summary>
    /// Initializes a new instance of the WorkflowPlanRevisionReference type.
    /// </summary>
    public WorkflowPlanRevisionReference(
        WorkflowPlanReferenceKind kind,
        string identifier,
        string revision,
        string? canonicalFingerprint)
    {
        Kind = kind;
        Identifier = IdentityText.Require(identifier, nameof(identifier), 512);
        Revision = IdentityText.Require(revision, nameof(revision), 256);
        CanonicalFingerprint = canonicalFingerprint is null
            ? null
            : IdentityText.RequireSha256(canonicalFingerprint, nameof(canonicalFingerprint));

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown workflow plan reference kind.");
        }

        if (kind == WorkflowPlanReferenceKind.BuiltInPreset && CanonicalFingerprint is not null)
        {
            throw new ArgumentException("A built-in preset does not accept a caller-supplied content fingerprint.", nameof(canonicalFingerprint));
        }

        if (kind == WorkflowPlanReferenceKind.FuwenDefinition && CanonicalFingerprint is null)
        {
            throw new ArgumentException("A Fuwen definition reference requires a canonical fingerprint.", nameof(canonicalFingerprint));
        }
    }

    /// <summary>
    /// Gets the Kind value.
    /// </summary>
    public WorkflowPlanReferenceKind Kind { get; }
    /// <summary>
    /// Gets the Identifier value.
    /// </summary>
    public string Identifier { get; }
    /// <summary>
    /// Gets the Revision value.
    /// </summary>
    public string Revision { get; }
    /// <summary>
    /// Gets the CanonicalFingerprint value.
    /// </summary>
    public string? CanonicalFingerprint { get; }

    /// <summary>
    /// Performs the BuiltInPreset contract operation.
    /// </summary>
    public static WorkflowPlanRevisionReference BuiltInPreset(string identifier, string version) =>
        new(WorkflowPlanReferenceKind.BuiltInPreset, identifier, version, null);

    /// <summary>
    /// Performs the FuwenDefinition contract operation.
    /// </summary>
    public static WorkflowPlanRevisionReference FuwenDefinition(
        string definitionIdentifier,
        string revision,
        string canonicalFingerprint) =>
        new(WorkflowPlanReferenceKind.FuwenDefinition, definitionIdentifier, revision, canonicalFingerprint);

    /// <summary>
    /// Validates this contract value and throws when an invariant is violated.
    /// </summary>
    public void Validate()
    {
        if (!Enum.IsDefined(Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "Unknown workflow plan reference kind.");
        }

        IdentityText.Require(Identifier, nameof(Identifier), 512);
        IdentityText.Require(Revision, nameof(Revision), 256);
        if (Kind == WorkflowPlanReferenceKind.FuwenDefinition)
        {
            IdentityText.RequireSha256(CanonicalFingerprint, nameof(CanonicalFingerprint));
        }
        else if (CanonicalFingerprint is not null)
        {
            throw new ArgumentException("A built-in preset does not accept a content fingerprint.", nameof(CanonicalFingerprint));
        }
    }
}

/// <summary>Host-supplied identity of the Hongxian session that contains this work.</summary>
public readonly record struct HongxianSessionReference
{
    /// <summary>
    /// Initializes a new instance of the HongxianSessionReference type.
    /// </summary>
    public HongxianSessionReference(string identifier)
    {
        Identifier = IdentityText.Require(identifier, nameof(identifier), 512);
    }

    /// <summary>
    /// Gets the Identifier value.
    /// </summary>
    public string Identifier { get; }

    /// <summary>
    /// Validates this contract value and throws when an invariant is violated.
    /// </summary>
    public void Validate() => IdentityText.Require(Identifier, nameof(Identifier), 512);
}

/// <summary>Provider-neutral workflow run and execution epoch identity.</summary>
public sealed record WorkflowRunExecutionReference
{
    /// <summary>
    /// Initializes a new instance of the WorkflowRunExecutionReference type.
    /// </summary>
    public WorkflowRunExecutionReference(string provider, string workflowRunId, string executionEpoch)
    {
        Provider = IdentityText.Require(provider, nameof(provider), 128);
        WorkflowRunId = IdentityText.Require(workflowRunId, nameof(workflowRunId), 512);
        ExecutionEpoch = IdentityText.Require(executionEpoch, nameof(executionEpoch), 256);
    }

    /// <summary>
    /// Gets the Provider value.
    /// </summary>
    public string Provider { get; }
    /// <summary>
    /// Gets the WorkflowRunId value.
    /// </summary>
    public string WorkflowRunId { get; }
    /// <summary>
    /// Gets the ExecutionEpoch value.
    /// </summary>
    public string ExecutionEpoch { get; }

    /// <summary>
    /// Validates this contract value and throws when an invariant is violated.
    /// </summary>
    public void Validate()
    {
        IdentityText.Require(Provider, nameof(Provider), 128);
        IdentityText.Require(WorkflowRunId, nameof(WorkflowRunId), 512);
        IdentityText.Require(ExecutionEpoch, nameof(ExecutionEpoch), 256);
    }
}

/// <summary>Stable structural node identity, distinct from runtime generations.</summary>
public readonly record struct StructuralNodeReference
{
    /// <summary>
    /// Initializes a new instance of the StructuralNodeReference type.
    /// </summary>
    public StructuralNodeReference(string identifier)
    {
        Identifier = IdentityText.Require(identifier, nameof(identifier), 512);
    }

    /// <summary>
    /// Gets the Identifier value.
    /// </summary>
    public string Identifier { get; }

    /// <summary>
    /// Validates this contract value and throws when an invariant is violated.
    /// </summary>
    public void Validate() => IdentityText.Require(Identifier, nameof(Identifier), 512);
}

/// <summary>Runtime generation of one structural node.</summary>
public readonly record struct NodeGenerationId
{
    /// <summary>
    /// Initializes a new instance of the NodeGenerationId type.
    /// </summary>
    public NodeGenerationId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A node generation identifier cannot be empty.", nameof(value));
        }

        Value = value;
    }

    /// <summary>
    /// Gets the Value value.
    /// </summary>
    public Guid Value { get; }

    /// <summary>
    /// Validates this contract value and throws when an invariant is violated.
    /// </summary>
    public void Validate() => IdentityText.RequireNonEmpty(Value, nameof(Value));
}

/// <summary>One provider attempt/handle within a node generation.</summary>
public sealed record ProviderExecutionAttemptReference
{
    /// <summary>
    /// Initializes a new instance of the ProviderExecutionAttemptReference type.
    /// </summary>
    public ProviderExecutionAttemptReference(string provider, string attemptId, string handle)
    {
        Provider = IdentityText.Require(provider, nameof(provider), 128);
        AttemptId = IdentityText.Require(attemptId, nameof(attemptId), 512);
        Handle = IdentityText.Require(handle, nameof(handle), 4_096);
    }

    /// <summary>
    /// Gets the Provider value.
    /// </summary>
    public string Provider { get; }
    /// <summary>
    /// Gets the AttemptId value.
    /// </summary>
    public string AttemptId { get; }
    /// <summary>
    /// Gets the Handle value.
    /// </summary>
    public string Handle { get; }

    /// <summary>
    /// Validates this contract value and throws when an invariant is violated.
    /// </summary>
    public void Validate()
    {
        IdentityText.Require(Provider, nameof(Provider), 128);
        IdentityText.Require(AttemptId, nameof(AttemptId), 512);
        IdentityText.Require(Handle, nameof(Handle), 4_096);
    }
}

/// <summary>Stable address of a supervisory checkpoint.</summary>
public readonly record struct SupervisorCheckpointId
{
    /// <summary>
    /// Initializes a new instance of the SupervisorCheckpointId type.
    /// </summary>
    public SupervisorCheckpointId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A supervisor checkpoint identifier cannot be empty.", nameof(value));
        }

        Value = value;
    }

    /// <summary>
    /// Gets the Value value.
    /// </summary>
    public Guid Value { get; }

    /// <summary>
    /// Validates this contract value and throws when an invariant is violated.
    /// </summary>
    public void Validate() => IdentityText.RequireNonEmpty(Value, nameof(Value));
}

/// <summary>
/// Immutable information needed to resume or inspect a waiting checkpoint.
/// It does not authorize an intervention and does not implement one.
/// </summary>
public sealed record SupervisorCheckpointDescriptor
{
    /// <summary>
    /// Initializes a new instance of the SupervisorCheckpointDescriptor type.
    /// </summary>
    public SupervisorCheckpointDescriptor(
        SupervisorCheckpointId checkpointId,
        HongxianSessionReference session,
        DelegationId delegationId,
        WorkflowPlanRevisionReference planRevision,
        WorkflowRunExecutionReference workflowRun,
        StructuralNodeReference structuralNode,
        NodeGenerationId nodeGeneration,
        long expectedObservableRevision,
        bool dependentProgressGated)
    {
        CheckpointId = checkpointId;
        Session = session;
        DelegationId = delegationId;
        PlanRevision = planRevision ?? throw new ArgumentNullException(nameof(planRevision));
        WorkflowRun = workflowRun ?? throw new ArgumentNullException(nameof(workflowRun));
        StructuralNode = structuralNode;
        NodeGeneration = nodeGeneration;
        ExpectedObservableRevision = expectedObservableRevision;
        DependentProgressGated = dependentProgressGated;
        if (!dependentProgressGated)
        {
            throw new ArgumentException(
                "A supervisor checkpoint must gate dependent progress; nonblocking attention is a wake hint.",
                nameof(dependentProgressGated));
        }

        Validate();
    }

    /// <summary>
    /// Gets the CheckpointId value.
    /// </summary>
    public SupervisorCheckpointId CheckpointId { get; }
    /// <summary>
    /// Gets the Session value.
    /// </summary>
    public HongxianSessionReference Session { get; }
    /// <summary>
    /// Gets the DelegationId value.
    /// </summary>
    public DelegationId DelegationId { get; }
    /// <summary>
    /// Gets the PlanRevision value.
    /// </summary>
    public WorkflowPlanRevisionReference PlanRevision { get; }
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
    /// Gets the ExpectedObservableRevision value.
    /// </summary>
    public long ExpectedObservableRevision { get; }
    /// <summary>
    /// Gets the DependentProgressGated value.
    /// </summary>
    public bool DependentProgressGated { get; }

    /// <summary>
    /// Validates this contract value and throws when an invariant is violated.
    /// </summary>
    public void Validate()
    {
        CheckpointId.Validate();
        Session.Validate();
        if (DelegationId.Value == Guid.Empty)
        {
            throw new ArgumentException("A checkpoint delegation identifier cannot be empty.", nameof(DelegationId));
        }

        PlanRevision.Validate();
        WorkflowRun.Validate();
        StructuralNode.Validate();
        NodeGeneration.Validate();
        ArgumentOutOfRangeException.ThrowIfNegative(ExpectedObservableRevision);
    }
}

/// <summary>Rules that preserve the distinction between attempts and generations.</summary>
public static class ExecutionIdentityRules
{
    /// <summary>
    /// Performs the EnsureRetryOrReconnectSameGeneration contract operation.
    /// </summary>
    public static void EnsureRetryOrReconnectSameGeneration(
        NodeGenerationId original,
        NodeGenerationId candidate)
    {
        original.Validate();
        candidate.Validate();
        if (original != candidate)
        {
            throw new InvalidOperationException("A retry or reconnect must remain in the same node generation.");
        }
    }

    /// <summary>
    /// Performs the EnsureSemanticReexecutionNewGeneration contract operation.
    /// </summary>
    public static void EnsureSemanticReexecutionNewGeneration(
        NodeGenerationId previous,
        NodeGenerationId candidate)
    {
        previous.Validate();
        candidate.Validate();
        if (previous == candidate)
        {
            throw new InvalidOperationException("Semantic node re-execution requires a new node generation.");
        }
    }

    /// <summary>
    /// Performs the EnsureReopenedWorkNewRunAndEpoch contract operation.
    /// </summary>
    public static void EnsureReopenedWorkNewRunAndEpoch(
        WorkflowRunExecutionReference previous,
        WorkflowRunExecutionReference candidate)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(candidate);
        previous.Validate();
        candidate.Validate();
        if (string.Equals(previous.WorkflowRunId, candidate.WorkflowRunId, StringComparison.Ordinal)
            || string.Equals(previous.ExecutionEpoch, candidate.ExecutionEpoch, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Reopening completed work requires a new linked workflow run and execution epoch.");
        }
    }
}

internal static class IdentityText
{
    /// <summary>
    /// Performs the Require contract operation.
    /// </summary>
    public static string Require(string? value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty identity value is required.", parameterName);
        }

        if (value.Length > maximumLength)
        {
            throw new ArgumentException($"The identity value cannot exceed {maximumLength} characters.", parameterName);
        }

        if (value.Normalize(System.Text.NormalizationForm.FormC) != value
            || value != value.Trim()
            || value.Any(char.IsControl))
        {
            throw new ArgumentException("Identity values must already be in canonical form.", parameterName);
        }

        return value;
    }

    /// <summary>
    /// Performs the RequireProse contract operation.
    /// </summary>
    public static string RequireProse(string? value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Non-empty prose is required.", parameterName);
        }

        if (value.Length > maximumLength)
        {
            throw new ArgumentException($"Prose cannot exceed {maximumLength} characters.", parameterName);
        }

        if (value.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
        {
            throw new ArgumentException("Prose cannot contain non-whitespace control characters.", parameterName);
        }

        return value.Normalize(System.Text.NormalizationForm.FormC)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
    }

    /// <summary>
    /// Performs the RequireSha256 contract operation.
    /// </summary>
    public static string RequireSha256(string? value, string parameterName)
    {
        Require(value, parameterName, 64);
        if (value!.Length != 64 || value.Any(character => !Uri.IsHexDigit(character) || char.IsUpper(character)))
        {
            throw new ArgumentException("A canonical SHA-256 fingerprint must contain 64 lowercase hexadecimal characters.", parameterName);
        }

        return value;
    }

    /// <summary>
    /// Performs the RequireNonEmpty contract operation.
    /// </summary>
    public static void RequireNonEmpty(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("The identifier cannot be empty.", parameterName);
        }
    }
}
