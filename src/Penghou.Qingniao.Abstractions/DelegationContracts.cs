namespace Penghou.Qingniao;

/// <summary>Identifies a delegation independently of any workflow provider run.</summary>
public readonly record struct DelegationId(Guid Value)
{
    /// <summary>
    /// Performs the New contract operation.
    /// </summary>
    public static DelegationId New() => new(Guid.NewGuid());

    /// <summary>
    /// Returns the canonical textual representation of this value.
    /// </summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Identifies a host-resolved workspace without granting arbitrary path access.</summary>
public sealed record WorkspaceReference
{
    /// <summary>
    /// Initializes a new instance of the WorkspaceReference type.
    /// </summary>
    public WorkspaceReference(string Provider, string Identifier, string? Revision = null)
    {
        this.Provider = Provider;
        this.Identifier = Identifier;
        this.Revision = Revision;
        Validate();
    }

    /// <summary>Gets the provider namespace of the workspace.</summary>
    public string Provider
    {
        get => IdentityText.Require(_provider, nameof(Provider), MaximumProviderLength);
        init => _provider = IdentityText.Require(value, nameof(Provider), MaximumProviderLength);
    }

    /// <summary>Gets the provider-assigned workspace identifier.</summary>
    public string Identifier
    {
        get => IdentityText.Require(_identifier, nameof(Identifier), MaximumIdentifierLength);
        init => _identifier = IdentityText.Require(value, nameof(Identifier), MaximumIdentifierLength);
    }

    /// <summary>Gets the optional immutable workspace revision.</summary>
    public string? Revision
    {
        get => _revision is null ? null : IdentityText.Require(_revision, nameof(Revision), MaximumRevisionLength);
        init => _revision = value is null ? null : IdentityText.Require(value, nameof(Revision), MaximumRevisionLength);
    }

    private string _provider = null!;
    private string _identifier = null!;
    private string? _revision;

    internal const int MaximumProviderLength = 128;
    internal const int MaximumIdentifierLength = 2_048;
    internal const int MaximumRevisionLength = 2_048;

    /// <summary>
    /// Validates this contract value and throws when an invariant is violated.
    /// </summary>
    public void Validate()
    {
        _ = Provider;
        _ = Identifier;
        _ = Revision;
    }
}

/// <summary>Identifies the durable workflow that executes a delegation.</summary>
public sealed record WorkflowReference
{
    /// <summary>
    /// Initializes a new instance of the WorkflowReference type.
    /// </summary>
    public WorkflowReference(string Provider, string Identifier)
    {
        this.Provider = Provider;
        this.Identifier = Identifier;
        Validate();
    }

    /// <summary>Gets the workflow provider namespace.</summary>
    public string Provider
    {
        get => IdentityText.Require(_provider, nameof(Provider), MaximumProviderLength);
        init => _provider = IdentityText.Require(value, nameof(Provider), MaximumProviderLength);
    }

    /// <summary>Gets the provider-assigned workflow identifier.</summary>
    public string Identifier
    {
        get => IdentityText.Require(_identifier, nameof(Identifier), MaximumIdentifierLength);
        init => _identifier = IdentityText.Require(value, nameof(Identifier), MaximumIdentifierLength);
    }

    private string _provider = null!;
    private string _identifier = null!;

    internal const int MaximumProviderLength = 128;
    internal const int MaximumIdentifierLength = 2_048;

    /// <summary>
    /// Validates this contract value and throws when an invariant is violated.
    /// </summary>
    public void Validate()
    {
        _ = Provider;
        _ = Identifier;
    }
}

/// <summary>Identifies an independently inspectable, owned result artifact.</summary>
public sealed record DelegationArtifactReference
{
    /// <summary>
    /// Initializes a new instance of the DelegationArtifactReference type.
    /// </summary>
    public DelegationArtifactReference(
        DelegationId delegationId,
        StructuralNodeReference structuralNode,
        NodeGenerationId nodeGeneration,
        string provider,
        string repository,
        string artifactId,
        string kind,
        int schemaVersion,
        string location,
        ArtifactContentIdentity contentIdentity)
    {
        ArtifactContracts.RequireDelegation(delegationId, nameof(delegationId));
        structuralNode.Validate();
        nodeGeneration.Validate();
        Provider = ArtifactContracts.Identity(provider, nameof(provider), 256);
        Repository = ArtifactContracts.Identity(repository, nameof(repository), 1_024);
        ArtifactId = ArtifactContracts.Identity(artifactId, nameof(artifactId), 1_024);
        Kind = ArtifactContracts.Identity(kind, nameof(kind), 256);
        if (schemaVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        }

        Location = ArtifactContracts.Identity(location, nameof(location), 4_096);
        contentIdentity.Validate();
        DelegationId = delegationId;
        StructuralNode = structuralNode;
        NodeGeneration = nodeGeneration;
        SchemaVersion = schemaVersion;
        ContentIdentity = contentIdentity;
    }

    /// <summary>
    /// Gets the DelegationId value.
    /// </summary>
    public DelegationId DelegationId { get; }
    /// <summary>
    /// Gets the StructuralNode value.
    /// </summary>
    public StructuralNodeReference StructuralNode { get; }
    /// <summary>
    /// Gets the NodeGeneration value.
    /// </summary>
    public NodeGenerationId NodeGeneration { get; }
    /// <summary>
    /// Gets the Provider value.
    /// </summary>
    public string Provider { get; }
    /// <summary>
    /// Gets the Repository value.
    /// </summary>
    public string Repository { get; }
    /// <summary>
    /// Gets the ArtifactId value.
    /// </summary>
    public string ArtifactId { get; }
    /// <summary>
    /// Gets the Kind value.
    /// </summary>
    public string Kind { get; }
    /// <summary>
    /// Gets the SchemaVersion value.
    /// </summary>
    public int SchemaVersion { get; }
    /// <summary>
    /// Gets the Location value.
    /// </summary>
    public string Location { get; }
    /// <summary>
    /// Gets the ContentIdentity value.
    /// </summary>
    public ArtifactContentIdentity ContentIdentity { get; }
}

/// <summary>
/// Represents the DelegationStrategy contract and its invariants.
/// </summary>
public enum DelegationStrategy
{
    /// <summary>
    /// Identifies the Implement enum value.
    /// </summary>
    Implement = 0,
    /// <summary>
    /// Identifies the Investigate enum value.
    /// </summary>
    Investigate = 1,
    /// <summary>
    /// Identifies the Review enum value.
    /// </summary>
    Review = 2,
    /// <summary>
    /// Identifies the Fix enum value.
    /// </summary>
    Fix = 3,
}

/// <summary>
/// Represents the DelegationState contract and its invariants.
/// </summary>
public enum DelegationState
{
    /// <summary>
    /// Identifies the Queued enum value.
    /// </summary>
    Queued = 0,
    /// <summary>
    /// Identifies the Running enum value.
    /// </summary>
    Running = 1,
    /// <summary>
    /// Identifies the Completed enum value.
    /// </summary>
    Completed = 2,
    /// <summary>
    /// Identifies the Failed enum value.
    /// </summary>
    Failed = 3,
    /// <summary>
    /// Identifies the Cancelled enum value.
    /// </summary>
    Cancelled = 4,
    /// <summary>
    /// Identifies the BudgetExceeded enum value.
    /// </summary>
    BudgetExceeded = 5,
    /// <summary>
    /// Identifies the NeedsSupervisor enum value.
    /// </summary>
    NeedsSupervisor = 6,
    /// <summary>
    /// Identifies the WaitingForSupervisor enum value.
    /// </summary>
    WaitingForSupervisor = 7,
}

/// <summary>
/// Represents the DelegationBudget contract and its invariants.
/// </summary>
public sealed record DelegationBudget
{
    /// <summary>
    /// Initializes a new instance of the DelegationBudget type.
    /// </summary>
    public DelegationBudget(
        int MaximumWorkerCalls = 8,
        int MaximumRetries = 1,
        TimeSpan? MaximumDuration = null,
        int MaximumParallelWorkers = 2)
    {
        this.MaximumWorkerCalls = MaximumWorkerCalls;
        this.MaximumRetries = MaximumRetries;
        this.MaximumDuration = MaximumDuration;
        this.MaximumParallelWorkers = MaximumParallelWorkers;
        Validate();
    }

    /// <summary>Gets or initializes the maximum number of worker calls permitted.</summary>
    public int MaximumWorkerCalls
    {
        get => RequireRange(_maximumWorkerCalls, 1, MaximumWorkerCallsLimit, nameof(MaximumWorkerCalls));
        init => _maximumWorkerCalls = RequireRange(value, 1, MaximumWorkerCallsLimit, nameof(MaximumWorkerCalls));
    }

    /// <summary>Gets or initializes the maximum number of retries permitted.</summary>
    public int MaximumRetries
    {
        get => RequireRange(_maximumRetries, 0, MaximumRetriesLimit, nameof(MaximumRetries));
        init => _maximumRetries = RequireRange(value, 0, MaximumRetriesLimit, nameof(MaximumRetries));
    }

    /// <summary>Gets or initializes the optional maximum execution duration.</summary>
    public TimeSpan? MaximumDuration
    {
        get
        {
            if (_maximumDuration is { } duration && (duration <= TimeSpan.Zero || duration > MaximumDurationLimit))
            {
                throw new ArgumentOutOfRangeException(nameof(MaximumDuration), duration, $"Maximum duration must be between 1 tick and {MaximumDurationLimit}.");
            }

            return _maximumDuration;
        }
        init
        {
            if (value is { } duration && (duration <= TimeSpan.Zero || duration > MaximumDurationLimit))
            {
                throw new ArgumentOutOfRangeException(nameof(MaximumDuration), duration, $"Maximum duration must be between 1 tick and {MaximumDurationLimit}.");
            }

            _maximumDuration = value;
        }
    }

    /// <summary>Gets or initializes the maximum number of parallel workers permitted.</summary>
    public int MaximumParallelWorkers
    {
        get => RequireRange(_maximumParallelWorkers, 1, MaximumParallelWorkersLimit, nameof(MaximumParallelWorkers));
        init => _maximumParallelWorkers = RequireRange(value, 1, MaximumParallelWorkersLimit, nameof(MaximumParallelWorkers));
    }

    internal const int MaximumWorkerCallsLimit = 1_000_000;
    internal const int MaximumRetriesLimit = 1_000_000;
    internal const int MaximumParallelWorkersLimit = 1_024;
    internal static readonly TimeSpan MaximumDurationLimit = TimeSpan.FromDays(365);

    private int _maximumWorkerCalls;
    private int _maximumRetries;
    private TimeSpan? _maximumDuration;
    private int _maximumParallelWorkers;

    /// <summary>
    /// Validates this contract value and throws when an invariant is violated.
    /// </summary>
    public void Validate()
    {
        _ = MaximumWorkerCalls;
        _ = MaximumRetries;
        _ = MaximumDuration;
        _ = MaximumParallelWorkers;
    }

    private static int RequireRange(int value, int minimum, int maximum, string parameterName)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, $"Value must be between {minimum} and {maximum}.");
        }

        return value;
    }
}

/// <summary>
/// A host-authenticated caller namespace. This identity is supplied by the
/// host boundary and is deliberately not part of <see cref="DelegationRequest"/>.
/// </summary>
public sealed record DelegationCallerScope
{
    /// <summary>
    /// Initializes a new instance of the DelegationCallerScope type.
    /// </summary>
    public DelegationCallerScope(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException("A caller scope identifier is required.", nameof(identifier));
        }

        if (identifier.Length > 256)
        {
            throw new ArgumentException("A caller scope identifier cannot exceed 256 characters.", nameof(identifier));
        }

        if (identifier.Normalize(System.Text.NormalizationForm.FormC) != identifier
            || identifier != identifier.Trim()
            || identifier.Contains('\r', StringComparison.Ordinal)
            || identifier.Contains('\n', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A caller scope identifier must already be in canonical form.",
                nameof(identifier));
        }

        Identifier = identifier;
    }

    /// <summary>
    /// Gets the Identifier value.
    /// </summary>
    public string Identifier { get; }
}

/// <summary>
/// A delegation request whose collection inputs are snapshotted at construction.
/// </summary>
public sealed record DelegationRequest
{
    /// <summary>
    /// Initializes a new instance of the DelegationRequest type.
    /// </summary>
    public DelegationRequest(
        string requestKey,
        string objective,
        WorkspaceReference workspace,
        IReadOnlyList<string> acceptanceCriteria,
        IReadOnlyList<string> constraints,
        DelegationBudget budget,
        DelegationStrategy strategy = DelegationStrategy.Implement,
        WorkflowPlanRevisionReference? planRevision = null)
    {
        RequestKey = requestKey;
        Objective = objective;
        Workspace = workspace;
        AcceptanceCriteria = Snapshot(acceptanceCriteria, nameof(acceptanceCriteria));
        Constraints = Snapshot(constraints, nameof(constraints));
        Budget = budget;
        Strategy = strategy;
        PlanRevision = planRevision;
    }

    /// <summary>
    /// Gets the RequestKey value.
    /// </summary>
    public string RequestKey { get; }
    /// <summary>
    /// Gets the Objective value.
    /// </summary>
    public string Objective { get; }
    /// <summary>
    /// Gets the Workspace value.
    /// </summary>
    public WorkspaceReference Workspace { get; }
    /// <summary>
    /// Gets the AcceptanceCriteria value.
    /// </summary>
    public IReadOnlyList<string> AcceptanceCriteria { get; }
    /// <summary>
    /// Gets the Constraints value.
    /// </summary>
    public IReadOnlyList<string> Constraints { get; }
    /// <summary>
    /// Gets the Budget value.
    /// </summary>
    public DelegationBudget Budget { get; }
    /// <summary>
    /// Gets the Strategy value.
    /// </summary>
    public DelegationStrategy Strategy { get; }
    /// <summary>
    /// The optional plan binding used by the v2 fingerprint contract. Existing
    /// v1 requests intentionally remain valid without this field.
    /// </summary>
    public WorkflowPlanRevisionReference? PlanRevision { get; }

    private static IReadOnlyList<string> Snapshot(IReadOnlyList<string> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (values.Count > 256)
        {
            throw new ArgumentException("A list cannot contain more than 256 values.", parameterName);
        }

        return Array.AsReadOnly(values.ToArray());
    }
}

/// <summary>
/// Represents the DelegationHandle contract and its invariants.
/// </summary>
public sealed record DelegationHandle(
    DelegationId DelegationId,
    WorkflowReference Workflow,
    DelegationState State);

/// <summary>
/// Represents the DelegationProgress contract and its invariants.
/// </summary>
public sealed record DelegationProgress
{
    /// <summary>
    /// Initializes a new instance of the DelegationProgress type.
    /// </summary>
    public DelegationProgress(
        DelegationId delegationId,
        DelegationState state,
        long revision,
        IReadOnlyList<string> currentSteps,
        IReadOnlyList<string> completedSteps,
        int workerCalls,
        int retries,
        DateTimeOffset updatedAt,
        SupervisorCheckpointDescriptor? checkpoint = null)
    {
        IdentityText.RequireNonEmpty(delegationId.Value, nameof(delegationId));
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown delegation state.");
        }

        if (revision is < 0 or > MaximumRevision)
        {
            throw new ArgumentOutOfRangeException(nameof(revision), revision, $"Revision must be between 0 and {MaximumRevision}.");
        }

        DelegationId = delegationId;
        State = state;
        Revision = revision;
        CurrentSteps = Snapshot(currentSteps, nameof(currentSteps));
        CompletedSteps = Snapshot(completedSteps, nameof(completedSteps));
        WorkerCalls = RequireCounter(workerCalls, nameof(workerCalls));
        Retries = RequireCounter(retries, nameof(retries));
        if (updatedAt == default)
        {
            throw new ArgumentException("A progress timestamp is required.", nameof(updatedAt));
        }

        UpdatedAt = updatedAt;
        Checkpoint = checkpoint;
    }

    /// <summary>
    /// Gets the DelegationId value.
    /// </summary>
    public DelegationId DelegationId { get; }
    /// <summary>
    /// Gets the State value.
    /// </summary>
    public DelegationState State { get; }
    /// <summary>
    /// Gets the Revision value.
    /// </summary>
    public long Revision { get; }
    /// <summary>
    /// Gets the CurrentSteps value.
    /// </summary>
    public IReadOnlyList<string> CurrentSteps { get; }
    /// <summary>
    /// Gets the CompletedSteps value.
    /// </summary>
    public IReadOnlyList<string> CompletedSteps { get; }
    /// <summary>
    /// Gets the WorkerCalls value.
    /// </summary>
    public int WorkerCalls { get; }
    /// <summary>
    /// Gets the Retries value.
    /// </summary>
    public int Retries { get; }
    /// <summary>
    /// Gets the UpdatedAt value.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; }
    /// <summary>
    /// Gets the Checkpoint value.
    /// </summary>
    public SupervisorCheckpointDescriptor? Checkpoint { get; }

    internal const int MaximumItems = 128;
    internal const int MaximumStepLength = 4_096;
    internal const long MaximumRevision = 1_000_000_000;
    internal const int MaximumCounter = 1_000_000;

    private static IReadOnlyList<string> Snapshot(IReadOnlyList<string> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (values.Count > MaximumItems)
        {
            throw new ArgumentException($"A progress collection cannot contain more than {MaximumItems} values.", parameterName);
        }

        var copy = new string[values.Count];
        for (var index = 0; index < values.Count; index++)
        {
            copy[index] = IdentityText.RequireProse(values[index], $"{parameterName}[{index}]", MaximumStepLength);
        }

        return Array.AsReadOnly(copy);
    }

    private static int RequireCounter(int value, string parameterName)
    {
        if (value is < 0 or > MaximumCounter)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, $"Progress counters must be between 0 and {MaximumCounter}.");
        }

        return value;
    }
}

/// <summary>
/// Represents the DelegationEvidence contract and its invariants.
/// </summary>
public sealed record DelegationEvidence
{
    /// <summary>
    /// Initializes a new instance of the DelegationEvidence type.
    /// </summary>
    public DelegationEvidence(
        IReadOnlyList<string> changedFiles,
        IReadOnlyList<string> commands,
        int testsPassed,
        int testsFailed,
        bool? reviewApproved,
        int reviewFindingsResolved)
    {
        ChangedFiles = SnapshotIdentities(changedFiles, nameof(changedFiles));
        Commands = SnapshotProse(commands, nameof(commands));
        TestsPassed = RequireCounter(testsPassed, nameof(testsPassed));
        TestsFailed = RequireCounter(testsFailed, nameof(testsFailed));
        ReviewApproved = reviewApproved;
        ReviewFindingsResolved = RequireCounter(reviewFindingsResolved, nameof(reviewFindingsResolved));
    }

    /// <summary>
    /// Gets the ChangedFiles value.
    /// </summary>
    public IReadOnlyList<string> ChangedFiles { get; }
    /// <summary>
    /// Gets the Commands value.
    /// </summary>
    public IReadOnlyList<string> Commands { get; }
    /// <summary>
    /// Gets the TestsPassed value.
    /// </summary>
    public int TestsPassed { get; }
    /// <summary>
    /// Gets the TestsFailed value.
    /// </summary>
    public int TestsFailed { get; }
    /// <summary>
    /// Gets the ReviewApproved value.
    /// </summary>
    public bool? ReviewApproved { get; }
    /// <summary>
    /// Gets the ReviewFindingsResolved value.
    /// </summary>
    public int ReviewFindingsResolved { get; }

    internal const int MaximumItems = 128;
    internal const int MaximumChangedFileLength = 4_096;
    internal const int MaximumCommandLength = 4_096;
    internal const int MaximumCounter = 1_000_000;

    private static IReadOnlyList<string> SnapshotIdentities(IReadOnlyList<string> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (values.Count > MaximumItems)
        {
            throw new ArgumentException($"An evidence collection cannot contain more than {MaximumItems} values.", parameterName);
        }

        var copy = new string[values.Count];
        for (var index = 0; index < values.Count; index++)
        {
            copy[index] = IdentityText.Require(values[index], $"{parameterName}[{index}]", MaximumChangedFileLength);
        }

        return Array.AsReadOnly(copy);
    }

    private static IReadOnlyList<string> SnapshotProse(IReadOnlyList<string> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (values.Count > MaximumItems)
        {
            throw new ArgumentException($"An evidence collection cannot contain more than {MaximumItems} values.", parameterName);
        }

        var copy = new string[values.Count];
        for (var index = 0; index < values.Count; index++)
        {
            copy[index] = IdentityText.RequireProse(values[index], $"{parameterName}[{index}]", MaximumCommandLength);
        }

        return Array.AsReadOnly(copy);
    }

    private static int RequireCounter(int value, string parameterName)
    {
        if (value is < 0 or > MaximumCounter)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, $"Evidence counters must be between 0 and {MaximumCounter}.");
        }

        return value;
    }
}

/// <summary>
/// Represents the DelegationResult contract and its invariants.
/// </summary>
public sealed record DelegationResult
{
    /// <summary>
    /// Initializes a new instance of the DelegationResult type.
    /// </summary>
    public DelegationResult(
        DelegationId delegationId,
        DelegationState state,
        string summary,
        DelegationEvidence evidence,
        IReadOnlyList<DelegationArtifactReference> artifacts,
        IReadOnlyList<string> unresolvedConcerns,
        DateTimeOffset completedAt,
        EvidenceBundle? normalizedEvidence = null,
        BudgetExceededOutcome? budgetExceeded = null)
    {
        IdentityText.RequireNonEmpty(delegationId.Value, nameof(delegationId));
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown delegation state.");
        }

        Summary = IdentityText.RequireProse(summary, nameof(summary), MaximumSummaryLength);
        ArgumentNullException.ThrowIfNull(evidence);
        if (completedAt == default)
        {
            throw new ArgumentException("A result completion timestamp is required.", nameof(completedAt));
        }

        DelegationId = delegationId;
        State = state;
        Evidence = evidence;
        Artifacts = SnapshotArtifacts(artifacts, delegationId, nameof(artifacts));
        UnresolvedConcerns = SnapshotConcerns(unresolvedConcerns, nameof(unresolvedConcerns));
        CompletedAt = completedAt;
        NormalizedEvidence = normalizedEvidence;
        if (budgetExceeded is not null && budgetExceeded.DelegationId != delegationId)
        {
            throw new ArgumentException("A budget-exceeded outcome must belong to the result delegation.", nameof(budgetExceeded));
        }

        BudgetExceeded = budgetExceeded;
        EvidenceContracts.ValidateBundleForDelegation(NormalizedEvidence, delegationId, nameof(normalizedEvidence));
    }

    /// <summary>
    /// Gets the DelegationId value.
    /// </summary>
    public DelegationId DelegationId { get; }
    /// <summary>
    /// Gets the State value.
    /// </summary>
    public DelegationState State { get; }
    /// <summary>
    /// Gets the Summary value.
    /// </summary>
    public string Summary { get; }
    /// <summary>
    /// Gets the Evidence value.
    /// </summary>
    public DelegationEvidence Evidence { get; }
    /// <summary>
    /// Gets the Artifacts value.
    /// </summary>
    public IReadOnlyList<DelegationArtifactReference> Artifacts { get; }
    /// <summary>
    /// Gets the UnresolvedConcerns value.
    /// </summary>
    public IReadOnlyList<string> UnresolvedConcerns { get; }
    /// <summary>
    /// Gets the CompletedAt value.
    /// </summary>
    public DateTimeOffset CompletedAt { get; }
    /// <summary>
    /// Optional bounded normalized evidence. Raw transcripts and provider
    /// payloads remain artifact references rather than result payloads.
    /// </summary>
    public EvidenceBundle? NormalizedEvidence { get; }
    /// <summary>
    /// Durable accounting evidence required when <see cref="State"/> is
    /// <see cref="DelegationState.BudgetExceeded"/>.
    /// </summary>
    public BudgetExceededOutcome? BudgetExceeded { get; }

    internal const int MaximumSummaryLength = 16_384;
    internal const int MaximumConcerns = 128;
    internal const int MaximumConcernLength = 4_096;

    private static IReadOnlyList<DelegationArtifactReference> SnapshotArtifacts(
        IReadOnlyList<DelegationArtifactReference> values,
        DelegationId delegationId,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (values.Count > ArtifactContracts.MaximumCollectionItems)
        {
            throw new ArgumentException($"A result artifact collection cannot contain more than {ArtifactContracts.MaximumCollectionItems} values.", parameterName);
        }

        var copy = values.ToArray();
        var identities = new HashSet<ArtifactContracts.ArtifactIdentityKey>();
        for (var index = 0; index < copy.Length; index++)
        {
            ArtifactContracts.ValidateArtifact(copy[index], delegationId);
            if (!identities.Add(ArtifactContracts.IdentityKey(copy[index])))
            {
                throw new ArgumentException("A result cannot contain duplicate artifact identities.", parameterName);
            }
        }

        return Array.AsReadOnly(copy);
    }

    private static IReadOnlyList<string> SnapshotConcerns(IReadOnlyList<string> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (values.Count > MaximumConcerns)
        {
            throw new ArgumentException($"A result cannot contain more than {MaximumConcerns} unresolved concerns.", parameterName);
        }

        var copy = new string[values.Count];
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < values.Count; index++)
        {
            copy[index] = IdentityText.RequireProse(values[index], $"{parameterName}[{index}]", MaximumConcernLength);
            if (!distinct.Add(copy[index]))
            {
                throw new ArgumentException("A result cannot contain duplicate unresolved concerns.", parameterName);
            }
        }

        return Array.AsReadOnly(copy);
    }
}

/// <summary>
/// Represents the IDelegationService contract and its invariants.
/// </summary>
public interface IDelegationService
{
    /// <summary>
    /// Performs the DelegateAsync contract operation.
    /// </summary>
    Task<DelegationHandle> DelegateAsync(
        DelegationCallerScope caller,
        DelegationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs the GetStatusAsync contract operation.
    /// </summary>
    Task<DelegationProgress?> GetStatusAsync(
        DelegationId id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs the GetResultAsync contract operation.
    /// </summary>
    Task<DelegationResult?> GetResultAsync(
        DelegationId id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs the CancelAsync contract operation.
    /// </summary>
    Task CancelAsync(
        DelegationId id,
        CancellationToken cancellationToken = default);
}
