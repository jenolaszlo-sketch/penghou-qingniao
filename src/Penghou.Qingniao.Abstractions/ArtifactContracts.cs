namespace Penghou.Qingniao;

/// <summary>
/// An explicit content-hash contract. Qingniao preserves unknown future
/// contracts without interpreting them; SHA-256 contracts require exact
/// lowercase hexadecimal values.
/// </summary>
public readonly record struct ArtifactContentIdentity
{
    /// <summary>
    /// Provides the Sha256BytesV1 contract constant.
    /// </summary>
    public const string Sha256BytesV1 = "sha256-bytes-v1";

    /// <summary>
    /// Initializes a new instance of the ArtifactContentIdentity type.
    /// </summary>
    public ArtifactContentIdentity(string contractVersion, string hash)
    {
        ContractVersion = ArtifactContracts.Version(contractVersion, nameof(contractVersion));
        Hash = ArtifactContracts.Hash(hash, nameof(hash), ContractVersion.StartsWith("sha256-", StringComparison.Ordinal));
    }

    /// <summary>
    /// Gets the ContractVersion value.
    /// </summary>
    public string ContractVersion { get; }
    /// <summary>
    /// Gets the Hash value.
    /// </summary>
    public string Hash { get; }

    /// <summary>
    /// Validates this contract value and throws when an invariant is violated.
    /// </summary>
    public void Validate()
    {
        ArtifactContracts.Version(ContractVersion, nameof(ContractVersion));
        ArtifactContracts.Hash(Hash, nameof(Hash), ContractVersion.StartsWith("sha256-", StringComparison.Ordinal));
    }

    /// <summary>
    /// Performs the Sha256Bytes contract operation.
    /// </summary>
    public static ArtifactContentIdentity Sha256Bytes(string hash) => new(Sha256BytesV1, hash);
}

/// <summary>
/// Represents the CandidateId contract and its invariants.
/// </summary>
public readonly record struct CandidateId
{
    /// <summary>
    /// Initializes a new instance of the CandidateId type.
    /// </summary>
    public CandidateId(Guid value)
    {
        if (value == Guid.Empty) throw new ArgumentException("A candidate identifier cannot be empty.", nameof(value));
        Value = value;
    }

    /// <summary>
    /// Gets the Value value.
    /// </summary>
    public Guid Value { get; }

    /// <summary>
    /// Validates this contract value and throws when an invariant is violated.
    /// </summary>
    public void Validate() => ArtifactContracts.RequireGuid(Value, nameof(Value));
    /// <summary>
    /// Returns the canonical textual representation of this value.
    /// </summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>
/// Represents the DelegationResultId contract and its invariants.
/// </summary>
public readonly record struct DelegationResultId
{
    /// <summary>
    /// Initializes a new instance of the DelegationResultId type.
    /// </summary>
    public DelegationResultId(Guid value)
    {
        if (value == Guid.Empty) throw new ArgumentException("A result identifier cannot be empty.", nameof(value));
        Value = value;
    }

    /// <summary>
    /// Gets the Value value.
    /// </summary>
    public Guid Value { get; }

    /// <summary>
    /// Validates this contract value and throws when an invariant is violated.
    /// </summary>
    public void Validate() => ArtifactContracts.RequireGuid(Value, nameof(Value));
    /// <summary>
    /// Returns the canonical textual representation of this value.
    /// </summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Immutable candidate revision containing references, never payloads.</summary>
public sealed record CandidateRevisionReference
{
    /// <summary>
    /// Initializes a new instance of the CandidateRevisionReference type.
    /// </summary>
    public CandidateRevisionReference(
        DelegationId delegationId,
        StructuralNodeReference structuralNode,
        NodeGenerationId nodeGeneration,
        CandidateId candidateId,
        int revision,
        ArtifactContentIdentity contentIdentity,
        IReadOnlyList<DelegationArtifactReference> artifacts,
        EvidenceBundle? evidence = null)
    {
        ArtifactContracts.RequireDelegation(delegationId, nameof(delegationId));
        structuralNode.Validate();
        nodeGeneration.Validate();
        candidateId.Validate();
        CandidateId = candidateId;
        if (revision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        contentIdentity.Validate();
        Artifacts = ArtifactContracts.Snapshot(artifacts, nameof(artifacts));
        if (Artifacts.Count == 0)
        {
            throw new ArgumentException("A candidate revision must reference at least one artifact.", nameof(artifacts));
        }

        var identities = new HashSet<ArtifactContracts.ArtifactIdentityKey>();
        foreach (var artifact in Artifacts)
        {
            ArtifactContracts.ValidateArtifact(artifact, delegationId, structuralNode, nodeGeneration);
            if (!identities.Add(ArtifactContracts.IdentityKey(artifact)))
            {
                throw new ArgumentException("A candidate revision cannot contain duplicate artifact identities.", nameof(artifacts));
            }
        }

        DelegationId = delegationId;
        StructuralNode = structuralNode;
        NodeGeneration = nodeGeneration;
        Revision = revision;
        ContentIdentity = contentIdentity;
        Evidence = evidence;
        EvidenceContracts.ValidateBundle(Evidence, this, nameof(evidence));
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
    /// Gets the CandidateId value.
    /// </summary>
    public CandidateId CandidateId { get; }
    /// <summary>
    /// Gets the Revision value.
    /// </summary>
    public int Revision { get; }
    /// <summary>
    /// Gets the ContentIdentity value.
    /// </summary>
    public ArtifactContentIdentity ContentIdentity { get; }
    /// <summary>
    /// Gets the Evidence value.
    /// </summary>
    public EvidenceBundle? Evidence { get; }
    /// <summary>
    /// Gets the Artifacts value.
    /// </summary>
    public IReadOnlyList<DelegationArtifactReference> Artifacts { get; }
}

/// <summary>Aggregate result identity that references a candidate and artifacts.</summary>
public sealed record DelegationResultReference
{
    /// <summary>
    /// Initializes a new instance of the DelegationResultReference type.
    /// </summary>
    public DelegationResultReference(
        DelegationId delegationId,
        DelegationResultId resultId,
        CandidateRevisionReference candidate,
        IReadOnlyList<DelegationArtifactReference> artifacts,
        EvidenceBundle? evidence = null)
    {
        ArtifactContracts.RequireDelegation(delegationId, nameof(delegationId));
        resultId.Validate();
        ResultId = resultId;
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.DelegationId != delegationId)
        {
            throw new ArgumentException("The candidate must belong to the result delegation.", nameof(candidate));
        }

        Candidate = candidate;
        Artifacts = ArtifactContracts.Snapshot(artifacts, nameof(artifacts));
        var identities = new HashSet<ArtifactContracts.ArtifactIdentityKey>();
        foreach (var artifact in Artifacts)
        {
            ArtifactContracts.ValidateArtifact(artifact, delegationId);
            if (!identities.Add(ArtifactContracts.IdentityKey(artifact)))
            {
                throw new ArgumentException("A result cannot contain duplicate artifact identities.", nameof(artifacts));
            }
        }

        DelegationId = delegationId;
        Evidence = evidence;
        // An aggregate result may collect validation/review evidence from
        // multiple node generations. Candidate publication remains bound to
        // one exact node generation; the aggregate is only delegation-scoped.
        EvidenceContracts.ValidateBundleForDelegation(Evidence, delegationId, nameof(evidence));
    }

    /// <summary>
    /// Gets the DelegationId value.
    /// </summary>
    public DelegationId DelegationId { get; }
    /// <summary>
    /// Gets the ResultId value.
    /// </summary>
    public DelegationResultId ResultId { get; }
    /// <summary>
    /// Gets the Candidate value.
    /// </summary>
    public CandidateRevisionReference Candidate { get; }
    /// <summary>
    /// Gets the Artifacts value.
    /// </summary>
    public IReadOnlyList<DelegationArtifactReference> Artifacts { get; }
    /// <summary>
    /// Gets the Evidence value.
    /// </summary>
    public EvidenceBundle? Evidence { get; }
}

/// <summary>
/// Represents the CandidateRevisionPublication contract and its invariants.
/// </summary>
public sealed record CandidateRevisionPublication(CandidateRevisionReference Candidate, bool IsNew);

/// <summary>
/// Represents the CandidateRevisionConflictException contract and its invariants.
/// </summary>
public sealed class CandidateRevisionConflictException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the CandidateRevisionConflictException type.
    /// </summary>
    public CandidateRevisionConflictException(string message) : base(message)
    {
    }
}

/// <summary>
/// Represents the ICandidateRevisionPublicationRegistry contract and its invariants.
/// </summary>
public interface ICandidateRevisionPublicationRegistry
{
    /// <summary>
    /// Performs the PublishAsync contract operation.
    /// </summary>
    ValueTask<CandidateRevisionPublication> PublishAsync(CandidateRevisionReference candidate, CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents the CandidateRevisionIdentity contract and its invariants.
/// </summary>
public static class CandidateRevisionIdentity
{
    /// <summary>
    /// Compares the candidate subject that evidence may reference without
    /// recursively comparing the evidence attached to that candidate.
    /// </summary>
    public static bool SubjectEqual(CandidateRevisionReference? left, CandidateRevisionReference? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        return left.DelegationId == right.DelegationId
            && left.StructuralNode == right.StructuralNode
            && left.NodeGeneration == right.NodeGeneration
            && left.CandidateId == right.CandidateId
            && left.Revision == right.Revision
            && left.ContentIdentity == right.ContentIdentity
            && left.Artifacts.SequenceEqual(right.Artifacts);
    }

    /// <summary>
    /// Performs the SemanticallyEqual contract operation.
    /// </summary>
    public static bool SemanticallyEqual(CandidateRevisionReference left, CandidateRevisionReference right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return SubjectEqual(left, right)
            && EvidenceBundleIdentity.SemanticallyEqual(left.Evidence, right.Evidence);
    }
}

internal static class ArtifactContracts
{
    /// <summary>
    /// Provides the MaximumCollectionItems contract constant.
    /// </summary>
    public const int MaximumCollectionItems = 128;

    /// <summary>
    /// Performs the Identity contract operation.
    /// </summary>
    public static string Identity(string? value, string parameterName, int maximumLength) => IdentityText.Require(value, parameterName, maximumLength);

    /// <summary>
    /// Performs the IdentityKey contract operation.
    /// </summary>
    public static ArtifactIdentityKey IdentityKey(DelegationArtifactReference artifact) =>
        new(artifact.Provider, artifact.Repository, artifact.ArtifactId);

    /// <summary>
    /// Performs the RequireDelegation contract operation.
    /// </summary>
    public static void RequireDelegation(DelegationId value, string parameterName) => IdentityText.RequireNonEmpty(value.Value, parameterName);
    /// <summary>
    /// Performs the RequireGuid contract operation.
    /// </summary>
    public static void RequireGuid(Guid value, string parameterName) => IdentityText.RequireNonEmpty(value, parameterName);

    /// <summary>
    /// Performs the Version contract operation.
    /// </summary>
    public static string Version(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 32
            || !IsAsciiLowercaseLetterOrDigit(value[0])
            || value.Any(character => !IsAsciiLowercaseLetterOrDigit(character) && character is not ('.' or '-' or '_')))
        {
            throw new ArgumentException("A bounded lowercase ASCII content-hash contract is required.", parameterName);
        }

        return value;
    }

    /// <summary>
    /// Performs the Hash contract operation.
    /// </summary>
    public static string Hash(string? value, string parameterName, bool sha256)
    {
        if (sha256)
        {
            return IdentityText.RequireSha256(value, parameterName);
        }

        return IdentityText.Require(value, parameterName, 512);
    }

    public static IReadOnlyList<T> Snapshot<T>(IReadOnlyList<T> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (values.Count > MaximumCollectionItems)
        {
            throw new ArgumentException($"A collection cannot contain more than {MaximumCollectionItems} values.", parameterName);
        }

        return Array.AsReadOnly(values.ToArray());
    }

    /// <summary>
    /// Performs the ValidateArtifact contract operation.
    /// </summary>
    public static void ValidateArtifact(DelegationArtifactReference artifact, DelegationId expectedDelegation, StructuralNodeReference? expectedNode = null, NodeGenerationId? expectedGeneration = null)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        artifact.StructuralNode.Validate();
        artifact.NodeGeneration.Validate();
        Identity(artifact.Provider, nameof(artifact.Provider), 256);
        Identity(artifact.Repository, nameof(artifact.Repository), 1_024);
        Identity(artifact.ArtifactId, nameof(artifact.ArtifactId), 1_024);
        Identity(artifact.Kind, nameof(artifact.Kind), 256);
        if (artifact.SchemaVersion < 1) throw new ArgumentOutOfRangeException(nameof(artifact.SchemaVersion));
        Identity(artifact.Location, nameof(artifact.Location), 4_096);
        if (artifact.DelegationId != expectedDelegation) throw new ArgumentException("Artifact ownership does not match the expected delegation.", nameof(artifact));
        if (expectedNode is { } node && artifact.StructuralNode != node) throw new ArgumentException("Artifact ownership does not match the expected structural node.", nameof(artifact));
        if (expectedGeneration is { } generation && artifact.NodeGeneration != generation) throw new ArgumentException("Artifact ownership does not match the expected node generation.", nameof(artifact));
        artifact.ContentIdentity.Validate();
    }

    internal readonly record struct ArtifactIdentityKey(string Provider, string Repository, string ArtifactId);

    private static bool IsAsciiLowercaseLetterOrDigit(char value) => value is >= 'a' and <= 'z' or >= '0' and <= '9';
}
