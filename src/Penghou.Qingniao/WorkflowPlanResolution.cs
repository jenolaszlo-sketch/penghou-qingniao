namespace Penghou.Qingniao;

/// <summary>Describes the outcome of host verification for an opaque plan reference.</summary>
public enum WorkflowPlanVerificationStatus
{
    /// <summary>The host verified the exact reference and its fingerprint.</summary>
    Verified = 0,

    /// <summary>The host does not know the referenced definition.</summary>
    Unknown = 1,

    /// <summary>The caller is not authorized to use the referenced definition.</summary>
    Unauthorized = 2,

    /// <summary>The referenced revision is no longer current.</summary>
    Stale = 3,

    /// <summary>The supplied fingerprint does not match the host's definition.</summary>
    FingerprintMismatch = 4,
}

/// <summary>Immutable host decision for one opaque Fuwen definition reference.</summary>
public sealed record WorkflowPlanVerificationResult
{
    /// <summary>Initializes a host verification result.</summary>
    public WorkflowPlanVerificationResult(WorkflowPlanVerificationStatus status)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown workflow plan verification status.");
        }

        Status = status;
    }

    /// <summary>Gets the host verification status.</summary>
    public WorkflowPlanVerificationStatus Status { get; }

    /// <summary>Gets whether the exact opaque reference was verified by the host.</summary>
    public bool IsVerified => Status == WorkflowPlanVerificationStatus.Verified;

    /// <summary>Creates a successful verification result.</summary>
    public static WorkflowPlanVerificationResult Verified() =>
        new(WorkflowPlanVerificationStatus.Verified);

    /// <summary>Creates a rejected verification result.</summary>
    public static WorkflowPlanVerificationResult Rejected(WorkflowPlanVerificationStatus status)
    {
        if (status == WorkflowPlanVerificationStatus.Verified)
        {
            throw new ArgumentException("A verified status must use Verified().", nameof(status));
        }

        return new WorkflowPlanVerificationResult(status);
    }
}

/// <summary>
/// Exact caller, workspace, and opaque plan identity supplied to host policy.
/// Hosts must authorize this complete context; Qingniao does not interpret a
/// Fuwen definition or recompute its fingerprint.
/// </summary>
public sealed class WorkflowPlanVerificationContext
{
    /// <summary>Initializes a bounded verification context.</summary>
    public WorkflowPlanVerificationContext(
        DelegationCallerScope caller,
        WorkflowPlanRevisionReference planRevision,
        WorkspaceReference workspace)
    {
        Caller = caller ?? throw new ArgumentNullException(nameof(caller));
        PlanRevision = planRevision ?? throw new ArgumentNullException(nameof(planRevision));
        Workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        PlanRevision.Validate();
        Workspace.Validate();
    }

    /// <summary>Gets the authenticated caller scope.</summary>
    public DelegationCallerScope Caller { get; }

    /// <summary>Gets the exact opaque plan revision reference.</summary>
    public WorkflowPlanRevisionReference PlanRevision { get; }

    /// <summary>Gets the host-resolved workspace being authorized.</summary>
    public WorkspaceReference Workspace { get; }
}

/// <summary>
/// Host policy seam for opaque Fuwen definition references. Qingniao passes the
/// reference through unchanged and does not parse, canonicalize, or interpret
/// a Fuwen definition.
/// </summary>
public interface IWorkflowPlanHostVerifier
{
    /// <summary>Verifies the exact caller, workspace, identifier, revision, authorization, and fingerprint.</summary>
    WorkflowPlanVerificationResult Verify(WorkflowPlanVerificationContext context);
}

/// <summary>Raised when a workflow plan cannot be resolved for execution.</summary>
internal sealed class WorkflowPlanResolutionException : InvalidOperationException
{
    /// <summary>Initializes a plan resolution failure.</summary>
    public WorkflowPlanResolutionException(
        string message,
        WorkflowPlanRevisionReference requestedPlanRevision,
        WorkflowPlanVerificationStatus status)
        : base(message)
    {
        RequestedPlanRevision = requestedPlanRevision ?? throw new ArgumentNullException(nameof(requestedPlanRevision));
        Status = status;
    }

    public WorkflowPlanResolutionException(
        string message,
        WorkflowPlanRevisionReference requestedPlanRevision,
        WorkflowPlanVerificationStatus status,
        Exception innerException)
        : base(message, innerException)
    {
        RequestedPlanRevision = requestedPlanRevision ?? throw new ArgumentNullException(nameof(requestedPlanRevision));
        Status = status;
    }

    /// <summary>Gets the plan reference that was rejected.</summary>
    public WorkflowPlanRevisionReference RequestedPlanRevision { get; }

    /// <summary>Gets the normalized rejection status.</summary>
    public WorkflowPlanVerificationStatus Status { get; }
}

/// <summary>Names actions in the fixed Implement definition.</summary>
internal enum WorkflowPlanStageKind
{
    Implement = 0,
    SealCandidate = 1,
    Test = 2,
    Review = 3,
    Evaluate = 4,
    Fix = 5,
    Result = 6,
}

internal static class WorkflowPlanStructuralIdentity
{
    public static void Add(string value, ISet<string> identifiers)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 128
            || value != value.Trim()
            || value.Normalize(System.Text.NormalizationForm.FormC) != value
            || value.Any(char.IsControl))
        {
            throw new InvalidOperationException("Workflow plan structural identifiers must be bounded canonical identity text.");
        }

        if (!identifiers.Add(value))
        {
            throw new InvalidOperationException($"Workflow plan structural identifier '{value}' is duplicated.");
        }
    }
}

/// <summary>One leaf action in the fixed Implement definition.</summary>
internal sealed record WorkflowPlanAction(string Identifier, WorkflowPlanStageKind Kind)
{
    public void Validate(ISet<string> identifiers)
    {
        WorkflowPlanStructuralIdentity.Add(Identifier, identifiers);

        if (!Enum.IsDefined(Kind))
        {
            throw new InvalidOperationException("Workflow plan contains an unknown action kind.");
        }
    }

}

/// <summary>Explicit parallel Test and Review pair in the fixed definition.</summary>
internal sealed record WorkflowPlanVerificationPair(
    string Identifier,
    WorkflowPlanAction Test,
    WorkflowPlanAction Review)
{
    public void Validate(ISet<string> identifiers)
    {
        AddIdentifier(Identifier, identifiers);
        Test.Validate(identifiers);
        Review.Validate(identifiers);
        if (Test.Kind != WorkflowPlanStageKind.Test || Review.Kind != WorkflowPlanStageKind.Review)
        {
            throw new InvalidOperationException("A verification pair must contain Test followed by independent Review.");
        }
    }

    private static void AddIdentifier(string value, ISet<string> identifiers)
    {
        WorkflowPlanStructuralIdentity.Add(value, identifiers);
    }
}

/// <summary>Explicit conditional one-fix branch in the fixed definition.</summary>
internal sealed record WorkflowPlanConditionalFix(
    string Identifier,
    WorkflowPlanAction Fix,
    WorkflowPlanVerificationPair Verification,
    int MaximumExecutions)
{
    public void Validate(ISet<string> identifiers)
    {
        if (MaximumExecutions != 1)
        {
            throw new InvalidOperationException("The fixed Implement definition permits at most one semantic fix.");
        }

        WorkflowPlanStructuralIdentity.Add(Identifier, identifiers);

        Fix.Validate(identifiers);
        Verification.Validate(identifiers);
    }
}

/// <summary>
/// The deliberately sealed, non-graph shape of Qingniao's built-in Implement
/// preset. Evaluation conditionally selects the one-fix branch; supervision is
/// coordinator policy and is not an unconditional plan stage.
/// </summary>
internal sealed class ImplementWorkflowPlanDefinition
{
    public ImplementWorkflowPlanDefinition(
        string identifier,
        WorkflowPlanAction implement,
        WorkflowPlanAction sealCandidate,
        WorkflowPlanVerificationPair initialVerification,
        WorkflowPlanAction evaluate,
        WorkflowPlanConditionalFix optionalFix,
        WorkflowPlanAction result)
    {
        ArgumentNullException.ThrowIfNull(implement);
        ArgumentNullException.ThrowIfNull(sealCandidate);
        ArgumentNullException.ThrowIfNull(initialVerification);
        ArgumentNullException.ThrowIfNull(evaluate);
        ArgumentNullException.ThrowIfNull(optionalFix);
        ArgumentNullException.ThrowIfNull(result);
        Identifier = identifier;
        Implement = implement;
        SealCandidate = sealCandidate;
        InitialVerification = initialVerification;
        Evaluate = evaluate;
        OptionalFix = optionalFix;
        Result = result;
    }

    public string Identifier { get; }
    public WorkflowPlanAction Implement { get; }
    public WorkflowPlanAction SealCandidate { get; }
    public WorkflowPlanVerificationPair InitialVerification { get; }
    public WorkflowPlanAction Evaluate { get; }
    public WorkflowPlanConditionalFix OptionalFix { get; }
    public WorkflowPlanAction Result { get; }

    public void Validate()
    {
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        WorkflowPlanStructuralIdentity.Add(Identifier, identifiers);

        Implement.Validate(identifiers);
        SealCandidate.Validate(identifiers);
        InitialVerification.Validate(identifiers);
        Evaluate.Validate(identifiers);
        OptionalFix.Validate(identifiers);
        Result.Validate(identifiers);
    }

    public static ImplementWorkflowPlanDefinition Create()
    {
        var definition = new ImplementWorkflowPlanDefinition(
            "implement-preset",
            new WorkflowPlanAction("implement", WorkflowPlanStageKind.Implement),
            new WorkflowPlanAction("seal-candidate", WorkflowPlanStageKind.SealCandidate),
            new WorkflowPlanVerificationPair(
                "test-and-review",
                new WorkflowPlanAction("test", WorkflowPlanStageKind.Test),
                new WorkflowPlanAction("review", WorkflowPlanStageKind.Review)),
            new WorkflowPlanAction("evaluate", WorkflowPlanStageKind.Evaluate),
            new WorkflowPlanConditionalFix(
                "optional-fix",
                new WorkflowPlanAction("fix", WorkflowPlanStageKind.Fix),
                new WorkflowPlanVerificationPair(
                    "fix-test-and-review",
                    new WorkflowPlanAction("fix-test", WorkflowPlanStageKind.Test),
                    new WorkflowPlanAction("fix-review", WorkflowPlanStageKind.Review)),
                MaximumExecutions: 1),
            new WorkflowPlanAction("result", WorkflowPlanStageKind.Result));
        definition.Validate();
        return definition;
    }
}

/// <summary>One resolved plan reference and its optional built-in structure.</summary>
internal sealed class WorkflowPlanResolution
{
    private WorkflowPlanResolution(
        WorkflowPlanRevisionReference planRevision,
        ImplementWorkflowPlanDefinition? definition,
        DelegationRequest? boundRequest)
    {
        PlanRevision = planRevision ?? throw new ArgumentNullException(nameof(planRevision));
        PlanRevision.Validate();
        Definition = definition;
        BoundRequest = boundRequest;
    }

    internal WorkflowPlanResolution(
        WorkflowPlanRevisionReference planRevision,
        ImplementWorkflowPlanDefinition? definition)
        : this(planRevision, definition, boundRequest: null)
    {
    }

    public WorkflowPlanRevisionReference PlanRevision { get; }
    public ImplementWorkflowPlanDefinition? Definition { get; }
    public DelegationRequest? BoundRequest { get; }
    public bool HasBuiltInStructure => Definition is not null;

    public WorkflowPlanResolution Bind(DelegationRequest boundRequest)
    {
        ArgumentNullException.ThrowIfNull(boundRequest);
        if (boundRequest.PlanRevision != PlanRevision)
        {
            throw new InvalidOperationException("A resolved request must be bound to the resolved plan revision.");
        }

        return new WorkflowPlanResolution(PlanRevision, Definition, boundRequest);
    }
}

/// <summary>Catalog boundary for immutable workflow plan revisions.</summary>
internal interface IWorkflowPlanCatalog
{
    /// <summary>Returns the catalog entry for an exact built-in reference, when present.</summary>
    bool TryGet(WorkflowPlanRevisionReference reference, out WorkflowPlanResolution? resolution);
}

/// <summary>
/// In-memory catalog containing only Qingniao's fixed Implement revision. It is
/// deliberately not a general graph or caller-authored plan registry.
/// </summary>
internal sealed class InMemoryWorkflowPlanCatalog : IWorkflowPlanCatalog
{
    /// <summary>The only built-in plan revision implemented by this runtime.</summary>
    public static WorkflowPlanRevisionReference ImplementRevision =>
        WorkflowPlanRevisionReference.BuiltInPreset("Implement", "1");

    private static readonly WorkflowPlanResolution ImplementResolution =
        new(ImplementRevision, ImplementWorkflowPlanDefinition.Create());

    /// <summary>Looks up only the exact built-in Implement/1 reference.</summary>
    public bool TryGet(WorkflowPlanRevisionReference reference, out WorkflowPlanResolution? resolution)
    {
        ArgumentNullException.ThrowIfNull(reference);
        reference.Validate();

        if (reference.Kind == WorkflowPlanReferenceKind.BuiltInPreset
            && string.Equals(reference.Identifier, "Implement", StringComparison.Ordinal)
            && string.Equals(reference.Revision, "1", StringComparison.Ordinal))
        {
            resolution = ImplementResolution;
            return true;
        }

        resolution = null;
        return false;
    }

}

/// <summary>Resolves northbound requests to immutable workflow plan revisions.</summary>
internal interface IWorkflowPlanResolver
{
    /// <summary>Validates and resolves a caller-scoped request before any execution begins.</summary>
    WorkflowPlanResolution Resolve(DelegationCallerScope caller, DelegationRequest request);
}

/// <summary>
/// Resolves planless requests to the fixed Implement/1 preset and gates opaque
/// Fuwen references through injected host verification.
/// </summary>
internal sealed class InMemoryWorkflowPlanResolver : IWorkflowPlanResolver
{
    private readonly IWorkflowPlanCatalog catalog;
    private readonly IWorkflowPlanHostVerifier? hostVerifier;

    public InMemoryWorkflowPlanResolver(IWorkflowPlanHostVerifier? hostVerifier = null)
        : this(new InMemoryWorkflowPlanCatalog(), hostVerifier)
    {
    }

    public InMemoryWorkflowPlanResolver(
        IWorkflowPlanCatalog catalog,
        IWorkflowPlanHostVerifier? hostVerifier = null)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.hostVerifier = hostVerifier;
    }

    public WorkflowPlanResolution Resolve(DelegationCallerScope caller, DelegationRequest request)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        DelegationRequestValidator.Validate(request);

        var requested = request.PlanRevision ?? InMemoryWorkflowPlanCatalog.ImplementRevision;
        requested.Validate();

        // Branch on kind before catalog lookup. A custom catalog must never
        // bypass host verification for an opaque Fuwen reference.
        if (requested.Kind == WorkflowPlanReferenceKind.FuwenDefinition)
        {
            if (hostVerifier is null)
            {
                throw Reject(requested, WorkflowPlanVerificationStatus.Unknown, "A host verifier is required for an opaque Fuwen definition reference.");
            }

            var context = new WorkflowPlanVerificationContext(caller, requested, request.Workspace);
            var verification = hostVerifier.Verify(context)
                ?? throw Reject(requested, WorkflowPlanVerificationStatus.Unknown, "The host verifier returned no decision.");
            if (!Enum.IsDefined(verification.Status))
            {
                throw Reject(requested, WorkflowPlanVerificationStatus.Unknown, "The host verifier returned an unknown decision.");
            }

            if (!verification.IsVerified)
            {
                throw Reject(requested, verification.Status, $"The host rejected workflow plan reference '{requested.Identifier}' at revision '{requested.Revision}'.");
            }

            return new WorkflowPlanResolution(requested, definition: null)
                .Bind(BindRequest(request, requested));
        }

        if (requested.Kind != WorkflowPlanReferenceKind.BuiltInPreset)
        {
            throw Reject(requested, WorkflowPlanVerificationStatus.Unknown, "The workflow plan reference kind is not supported.");
        }

        if (!catalog.TryGet(requested, out var resolution) || resolution is null)
        {
            throw Reject(requested, WorkflowPlanVerificationStatus.Unknown, "The requested built-in workflow plan revision is not registered.");
        }

        if (resolution.PlanRevision != requested || resolution.Definition is null)
        {
            throw Reject(requested, WorkflowPlanVerificationStatus.Unknown, "The workflow plan catalog returned a malformed entry.");
        }

        try
        {
            resolution.Definition.Validate();
        }
        catch (InvalidOperationException exception)
        {
            throw Reject(requested, WorkflowPlanVerificationStatus.Unknown, "The workflow plan catalog returned an invalid fixed definition.", exception);
        }

        return resolution.Bind(BindRequest(request, requested));
    }

    private static DelegationRequest BindRequest(
        DelegationRequest request,
        WorkflowPlanRevisionReference planRevision) =>
        new(
            request.RequestKey,
            request.Objective,
            request.Workspace,
            request.AcceptanceCriteria,
            request.Constraints,
            request.Budget,
            request.Strategy,
            planRevision);

    private static WorkflowPlanResolutionException Reject(
        WorkflowPlanRevisionReference reference,
        WorkflowPlanVerificationStatus status,
        string message,
        Exception? innerException = null) =>
        innerException is null
            ? new WorkflowPlanResolutionException(message, reference, status)
            : new WorkflowPlanResolutionException(message, reference, status, innerException);
}
