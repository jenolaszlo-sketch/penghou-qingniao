namespace Penghou.Qingniao;

/// <summary>
/// Host-authenticated supervisor identity. It is supplied by the host
/// boundary, never accepted from model-controlled request content.
/// </summary>
public sealed record SupervisorIdentity
{
    /// <summary>
    /// Initializes a new instance of the SupervisorIdentity type.
    /// </summary>
    public SupervisorIdentity(string authorityScope, string subject)
    {
        AuthorityScope = IdentityText.Require(authorityScope, nameof(authorityScope), 256);
        Subject = IdentityText.Require(subject, nameof(subject), 512);
    }

    /// <summary>
    /// Gets the AuthorityScope value.
    /// </summary>
    public string AuthorityScope { get; }
    /// <summary>
    /// Gets the Subject value.
    /// </summary>
    public string Subject { get; }

    /// <summary>
    /// Validates this contract value and throws when an invariant is violated.
    /// </summary>
    public void Validate()
    {
        IdentityText.Require(AuthorityScope, nameof(AuthorityScope), 256);
        IdentityText.Require(Subject, nameof(Subject), 512);
    }
}

/// <summary>
/// An opaque requested executor profile to be resolved and authorized by the
/// host. It grants no authority and is not a credential, endpoint, or
/// authorization decision. Host policy must validate it before execution.
/// </summary>
public sealed record ExecutorProfileReference
{
    /// <summary>
    /// Initializes a new instance of the ExecutorProfileReference type.
    /// </summary>
    public ExecutorProfileReference(string provider, string profile)
    {
        Provider = IdentityText.Require(provider, nameof(provider), 128);
        Profile = IdentityText.Require(profile, nameof(profile), 256);
    }

    /// <summary>
    /// Gets the Provider value.
    /// </summary>
    public string Provider { get; }
    /// <summary>
    /// Gets the Profile value.
    /// </summary>
    public string Profile { get; }

    /// <summary>
    /// Validates this contract value and throws when an invariant is violated.
    /// </summary>
    public void Validate()
    {
        IdentityText.Require(Provider, nameof(Provider), 128);
        IdentityText.Require(Profile, nameof(Profile), 256);
    }
}

/// <summary>
/// A closed, typed set of supervisor actions. Each action can carry only the
/// fields meaningful for that action; arbitrary mutation payloads are not
/// representable through this contract.
/// </summary>
public abstract record SupervisorAction
{
    private SupervisorAction()
    {
    }

    /// <summary>
    /// Gets the Kind value.
    /// </summary>
    public abstract string Kind { get; }
    /// <summary>
    /// Validates this contract value and throws when an invariant is violated.
    /// </summary>
    public abstract void Validate();

    /// <summary>
    /// Represents the Respond contract and its invariants.
    /// </summary>
    public sealed record Respond : SupervisorAction
    {
        /// <summary>
        /// Initializes a new instance of the Respond type.
        /// </summary>
        public Respond(string response) => Response = IdentityText.RequireProse(response, nameof(response), 16_384);
        /// <summary>
        /// Gets the Response value.
        /// </summary>
        public string Response { get; }
        /// <summary>
        /// Gets the Kind value.
        /// </summary>
        public override string Kind => "respond";
        /// <summary>
        /// Validates this contract value and throws when an invariant is violated.
        /// </summary>
        public override void Validate() => IdentityText.RequireProse(Response, nameof(Response), 16_384);
    }

    /// <summary>
    /// Represents the Approve contract and its invariants.
    /// </summary>
    public sealed record Approve : SupervisorAction
    {
        /// <summary>
        /// Initializes a new instance of the Approve type.
        /// </summary>
        public Approve(string? rationale = null) => Rationale = rationale is null
            ? null
            : IdentityText.RequireProse(rationale, nameof(rationale), 4_096);
        /// <summary>
        /// Gets the Rationale value.
        /// </summary>
        public string? Rationale { get; }
        /// <summary>
        /// Gets the Kind value.
        /// </summary>
        public override string Kind => "approve";
        /// <summary>
        /// Validates this contract value and throws when an invariant is violated.
        /// </summary>
        public override void Validate()
        {
            if (Rationale is not null)
            {
                IdentityText.RequireProse(Rationale, nameof(Rationale), 4_096);
            }
        }
    }

    /// <summary>
    /// Represents the Reject contract and its invariants.
    /// </summary>
    public sealed record Reject : SupervisorAction
    {
        /// <summary>
        /// Initializes a new instance of the Reject type.
        /// </summary>
        public Reject(string reason) => Reason = IdentityText.RequireProse(reason, nameof(reason), 4_096);
        /// <summary>
        /// Gets the Reason value.
        /// </summary>
        public string Reason { get; }
        /// <summary>
        /// Gets the Kind value.
        /// </summary>
        public override string Kind => "reject";
        /// <summary>
        /// Validates this contract value and throws when an invariant is violated.
        /// </summary>
        public override void Validate() => IdentityText.RequireProse(Reason, nameof(Reason), 4_096);
    }

    /// <summary>
    /// Represents the Retry contract and its invariants.
    /// </summary>
    public sealed record Retry : SupervisorAction
    {
        /// <summary>
        /// Initializes a new instance of the Retry type.
        /// </summary>
        public Retry(string reason) => Reason = IdentityText.RequireProse(reason, nameof(reason), 1_024);
        /// <summary>
        /// Gets the Reason value.
        /// </summary>
        public string Reason { get; }
        /// <summary>
        /// Gets the Kind value.
        /// </summary>
        public override string Kind => "retry";
        /// <summary>
        /// Validates this contract value and throws when an invariant is violated.
        /// </summary>
        public override void Validate() => IdentityText.RequireProse(Reason, nameof(Reason), 1_024);
    }

    /// <summary>
    /// Represents the ReexecuteNode contract and its invariants.
    /// </summary>
    public sealed record ReexecuteNode : SupervisorAction
    {
        /// <summary>
        /// Initializes a new instance of the ReexecuteNode type.
        /// </summary>
        public ReexecuteNode(StructuralNodeReference target, string reason)
        {
            Target = target;
            Target.Validate();
            Reason = IdentityText.RequireProse(reason, nameof(reason), 4_096);
        }

        /// <summary>
        /// Gets the Target value.
        /// </summary>
        public StructuralNodeReference Target { get; }
        /// <summary>
        /// Gets the Reason value.
        /// </summary>
        public string Reason { get; }
        /// <summary>
        /// Gets the Kind value.
        /// </summary>
        public override string Kind => "reexecute-node";
        /// <summary>
        /// Validates this contract value and throws when an invariant is violated.
        /// </summary>
        public override void Validate()
        {
            Target.Validate();
            IdentityText.RequireProse(Reason, nameof(Reason), 4_096);
        }
    }

    /// <summary>
    /// Represents the ReexecuteSubgraph contract and its invariants.
    /// </summary>
    public sealed record ReexecuteSubgraph : SupervisorAction
    {
        /// <summary>
        /// Initializes a new instance of the ReexecuteSubgraph type.
        /// </summary>
        public ReexecuteSubgraph(StructuralNodeReference root, string reason)
        {
            Root = root;
            Root.Validate();
            Reason = IdentityText.RequireProse(reason, nameof(reason), 4_096);
        }

        /// <summary>
        /// Gets the Root value.
        /// </summary>
        public StructuralNodeReference Root { get; }
        /// <summary>
        /// Gets the Reason value.
        /// </summary>
        public string Reason { get; }
        /// <summary>
        /// Gets the Kind value.
        /// </summary>
        public override string Kind => "reexecute-subgraph";
        /// <summary>
        /// Validates this contract value and throws when an invariant is violated.
        /// </summary>
        public override void Validate()
        {
            Root.Validate();
            IdentityText.RequireProse(Reason, nameof(Reason), 4_096);
        }
    }

    /// <summary>
    /// Represents the AddConstraint contract and its invariants.
    /// </summary>
    public sealed record AddConstraint : SupervisorAction
    {
        /// <summary>
        /// Initializes a new instance of the AddConstraint type.
        /// </summary>
        public AddConstraint(string constraint) => Constraint = IdentityText.RequireProse(constraint, nameof(constraint), 4_096);
        /// <summary>
        /// Gets the Constraint value.
        /// </summary>
        public string Constraint { get; }
        /// <summary>
        /// Gets the Kind value.
        /// </summary>
        public override string Kind => "add-constraint";
        /// <summary>
        /// Validates this contract value and throws when an invariant is violated.
        /// </summary>
        public override void Validate() => IdentityText.RequireProse(Constraint, nameof(Constraint), 4_096);
    }

    /// <summary>
    /// Represents the ChangeExecutor contract and its invariants.
    /// </summary>
    public sealed record ChangeExecutor : SupervisorAction
    {
        /// <summary>
        /// Initializes a new instance of the ChangeExecutor type.
        /// </summary>
        public ChangeExecutor(ExecutorProfileReference profile, string reason)
        {
            Profile = profile ?? throw new ArgumentNullException(nameof(profile));
            Profile.Validate();
            Reason = IdentityText.RequireProse(reason, nameof(reason), 4_096);
        }

        /// <summary>
        /// Gets the Profile value.
        /// </summary>
        public ExecutorProfileReference Profile { get; }
        /// <summary>
        /// Gets the Reason value.
        /// </summary>
        public string Reason { get; }
        /// <summary>
        /// Gets the Kind value.
        /// </summary>
        public override string Kind => "change-executor";
        /// <summary>
        /// Validates this contract value and throws when an invariant is violated.
        /// </summary>
        public override void Validate()
        {
            Profile.Validate();
            IdentityText.RequireProse(Reason, nameof(Reason), 4_096);
        }
    }

    /// <summary>
    /// Represents the SelectAlternative contract and its invariants.
    /// </summary>
    public sealed record SelectAlternative : SupervisorAction
    {
        /// <summary>
        /// Initializes a new instance of the SelectAlternative type.
        /// </summary>
        public SelectAlternative(string alternativeId, string rationale)
        {
            AlternativeId = IdentityText.Require(alternativeId, nameof(alternativeId), 512);
            Rationale = IdentityText.RequireProse(rationale, nameof(rationale), 4_096);
        }

        /// <summary>
        /// Gets the AlternativeId value.
        /// </summary>
        public string AlternativeId { get; }
        /// <summary>
        /// Gets the Rationale value.
        /// </summary>
        public string Rationale { get; }
        /// <summary>
        /// Gets the Kind value.
        /// </summary>
        public override string Kind => "select-alternative";
        /// <summary>
        /// Validates this contract value and throws when an invariant is violated.
        /// </summary>
        public override void Validate()
        {
            IdentityText.Require(AlternativeId, nameof(AlternativeId), 512);
            IdentityText.RequireProse(Rationale, nameof(Rationale), 4_096);
        }
    }

    /// <summary>
    /// Represents the Escalate contract and its invariants.
    /// </summary>
    public sealed record Escalate : SupervisorAction
    {
        /// <summary>
        /// Initializes a new instance of the Escalate type.
        /// </summary>
        public Escalate(string reason) => Reason = IdentityText.RequireProse(reason, nameof(reason), 4_096);
        /// <summary>
        /// Gets the Reason value.
        /// </summary>
        public string Reason { get; }
        /// <summary>
        /// Gets the Kind value.
        /// </summary>
        public override string Kind => "escalate";
        /// <summary>
        /// Validates this contract value and throws when an invariant is violated.
        /// </summary>
        public override void Validate() => IdentityText.RequireProse(Reason, nameof(Reason), 4_096);
    }

    /// <summary>
    /// Represents the Cancel contract and its invariants.
    /// </summary>
    public sealed record Cancel : SupervisorAction
    {
        /// <summary>
        /// Initializes a new instance of the Cancel type.
        /// </summary>
        public Cancel(string reason) => Reason = IdentityText.RequireProse(reason, nameof(reason), 4_096);
        /// <summary>
        /// Gets the Reason value.
        /// </summary>
        public string Reason { get; }
        /// <summary>
        /// Gets the Kind value.
        /// </summary>
        public override string Kind => "cancel";
        /// <summary>
        /// Validates this contract value and throws when an invariant is violated.
        /// </summary>
        public override void Validate() => IdentityText.RequireProse(Reason, nameof(Reason), 4_096);
    }
}

/// <summary>
/// A host-authenticated, caller-scoped intervention request. The supervisor
/// identity is deliberately passed separately to the acceptance boundary.
/// </summary>
public sealed record SupervisorIntervention
{
    /// <summary>
    /// Initializes a new instance of the SupervisorIntervention type.
    /// </summary>
    public SupervisorIntervention(
        DelegationId delegationId,
        SupervisorCheckpointId checkpointId,
        string interventionKey,
        long expectedRevision,
        SupervisorAction action)
    {
        if (delegationId.Value == Guid.Empty)
        {
            throw new ArgumentException("A delegation identifier cannot be empty.", nameof(delegationId));
        }

        DelegationId = delegationId;
        CheckpointId = checkpointId;
        CheckpointId.Validate();
        InterventionKey = IdentityText.Require(interventionKey, nameof(interventionKey), 256);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        ExpectedRevision = expectedRevision;
        Action = action ?? throw new ArgumentNullException(nameof(action));
        Action.Validate();
    }

    /// <summary>
    /// Gets the DelegationId value.
    /// </summary>
    public DelegationId DelegationId { get; }
    /// <summary>
    /// Gets the CheckpointId value.
    /// </summary>
    public SupervisorCheckpointId CheckpointId { get; }
    /// <summary>
    /// Gets the InterventionKey value.
    /// </summary>
    public string InterventionKey { get; }
    /// <summary>
    /// Gets the ExpectedRevision value.
    /// </summary>
    public long ExpectedRevision { get; }
    /// <summary>
    /// Gets the Action value.
    /// </summary>
    public SupervisorAction Action { get; }

    /// <summary>
    /// Validates this contract value and throws when an invariant is violated.
    /// </summary>
    public void Validate()
    {
        if (DelegationId.Value == Guid.Empty)
        {
            throw new ArgumentException("A delegation identifier cannot be empty.", nameof(DelegationId));
        }

        CheckpointId.Validate();
        IdentityText.Require(InterventionKey, nameof(InterventionKey), 256);
        ArgumentOutOfRangeException.ThrowIfNegative(ExpectedRevision);
        Action.Validate();
    }
}

/// <summary>Non-authorizing request to schedule or surface supervisor attention.</summary>
public sealed record WakeHint
{
    /// <summary>
    /// Initializes a new instance of the WakeHint type.
    /// </summary>
    public WakeHint(
        DelegationId delegationId,
        SupervisorCheckpointId? checkpointId,
        string reason,
        long afterRevision,
        DateTimeOffset? expiresAt = null)
    {
        if (delegationId.Value == Guid.Empty)
        {
            throw new ArgumentException("A delegation identifier cannot be empty.", nameof(delegationId));
        }

        checkpointId?.Validate();
        DelegationId = delegationId;
        CheckpointId = checkpointId;
        Reason = IdentityText.RequireProse(reason, nameof(reason), 2_048);
        ArgumentOutOfRangeException.ThrowIfNegative(afterRevision);
        AfterRevision = afterRevision;
        if (expiresAt is { } value && value == default)
        {
            throw new ArgumentException("A wake-hint expiry must be a real timestamp when supplied.", nameof(expiresAt));
        }

        ExpiresAt = expiresAt;
    }

    /// <summary>
    /// Gets the DelegationId value.
    /// </summary>
    public DelegationId DelegationId { get; }
    /// <summary>
    /// Gets the CheckpointId value.
    /// </summary>
    public SupervisorCheckpointId? CheckpointId { get; }
    /// <summary>
    /// Gets the Reason value.
    /// </summary>
    public string Reason { get; }
    /// <summary>
    /// Gets the AfterRevision value.
    /// </summary>
    public long AfterRevision { get; }
    /// <summary>
    /// Gets the ExpiresAt value.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; }
}
