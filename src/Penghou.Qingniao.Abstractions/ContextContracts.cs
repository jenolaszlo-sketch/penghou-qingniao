namespace Penghou.Qingniao;

/// <summary>
/// Represents the SupervisorContextFacet contract and its invariants.
/// </summary>
[Flags]
public enum SupervisorContextFacet
{
    /// <summary>
    /// Identifies the None enum value.
    /// </summary>
    None = 0,
    /// <summary>
    /// Identifies the Status enum value.
    /// </summary>
    Status = 1,
    /// <summary>
    /// Identifies the Summary enum value.
    /// </summary>
    Summary = 2,
    /// <summary>
    /// Identifies the Artifacts enum value.
    /// </summary>
    Artifacts = 4,
    /// <summary>
    /// Identifies the Correlations enum value.
    /// </summary>
    Correlations = 8,
    /// <summary>
    /// Identifies the PrimitiveReferences enum value.
    /// </summary>
    PrimitiveReferences = 16,
}

/// <summary>
/// Represents the ContextFacetAvailability contract and its invariants.
/// </summary>
public enum ContextFacetAvailability
{
    /// <summary>
    /// Identifies the Included enum value.
    /// </summary>
    Included = 0,
    /// <summary>
    /// Identifies the Truncated enum value.
    /// </summary>
    Truncated = 1,
    /// <summary>
    /// Identifies the Omitted enum value.
    /// </summary>
    Omitted = 2,
}

/// <summary>Explicitly records whether a requested context facet was included.</summary>
public sealed record ContextFacetOutcome
{
    /// <summary>
    /// Initializes a new instance of the ContextFacetOutcome type.
    /// </summary>
    public ContextFacetOutcome(
        SupervisorContextFacet facet,
        ContextFacetAvailability availability,
        int itemCount,
        string? reason = null)
    {
        ContextContracts.RequireSingleFacet(facet, nameof(facet));
        ArgumentOutOfRangeException.ThrowIfNegative(itemCount);
        if (availability is not (ContextFacetAvailability.Included
            or ContextFacetAvailability.Truncated
            or ContextFacetAvailability.Omitted))
        {
            throw new ArgumentOutOfRangeException(nameof(availability), availability, "Unknown context facet availability.");
        }

        if (availability == ContextFacetAvailability.Omitted && itemCount != 0)
        {
            throw new ArgumentException("An omitted facet cannot report included items.", nameof(itemCount));
        }

        if (availability != ContextFacetAvailability.Included && string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Truncation and omission require an explicit reason.", nameof(reason));
        }

        Facet = facet;
        Availability = availability;
        ItemCount = itemCount;
        Reason = reason is null ? null : ContextContracts.Prose(reason, nameof(reason), 1_024);
    }

    /// <summary>
    /// Gets the Facet value.
    /// </summary>
    public SupervisorContextFacet Facet { get; }
    /// <summary>
    /// Gets the Availability value.
    /// </summary>
    public ContextFacetAvailability Availability { get; }
    /// <summary>
    /// Gets the ItemCount value.
    /// </summary>
    public int ItemCount { get; }
    /// <summary>
    /// Gets the Reason value.
    /// </summary>
    public string? Reason { get; }
}

/// <summary>Hard limits applied to one context response.</summary>
public sealed record SupervisorContextLimits
{
    /// <summary>
    /// Initializes a new instance of the SupervisorContextLimits type.
    /// </summary>
    public SupervisorContextLimits(int maxItems, int maxInlineSummaryBytes)
    {
        if (maxItems is < 1 or > ContextContracts.MaximumItems)
        {
            throw new ArgumentOutOfRangeException(nameof(maxItems), maxItems, $"maxItems must be between 1 and {ContextContracts.MaximumItems}.");
        }

        if (maxInlineSummaryBytes is < 1 or > ContextContracts.MaximumInlineSummaryBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maxInlineSummaryBytes), maxInlineSummaryBytes, $"maxInlineSummaryBytes must be between 1 and {ContextContracts.MaximumInlineSummaryBytes}.");
        }

        MaxItems = maxItems;
        MaxInlineSummaryBytes = maxInlineSummaryBytes;
    }

    /// <summary>
    /// Gets the MaxItems value.
    /// </summary>
    public int MaxItems { get; }
    /// <summary>
    /// Gets the MaxInlineSummaryBytes value.
    /// </summary>
    public int MaxInlineSummaryBytes { get; }
}

/// <summary>Bounded, explicit request for checkpoint re-entry context.</summary>
public sealed record SupervisorContextRequest
{
    /// <summary>
    /// Initializes a new instance of the SupervisorContextRequest type.
    /// </summary>
    public SupervisorContextRequest(
        DelegationId delegationId,
        SupervisorCheckpointId checkpointId,
        long expectedRevision,
        SupervisorContextFacet requestedFacets,
        SupervisorContextLimits limits)
    {
        ContextContracts.RequireDelegation(delegationId, nameof(delegationId));
        checkpointId.Validate();
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        ContextContracts.RequireFacets(requestedFacets, nameof(requestedFacets));
        ArgumentNullException.ThrowIfNull(limits);
        DelegationId = delegationId;
        CheckpointId = checkpointId;
        ExpectedRevision = expectedRevision;
        RequestedFacets = requestedFacets;
        Limits = limits;
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
    /// Gets the ExpectedRevision value.
    /// </summary>
    public long ExpectedRevision { get; }
    /// <summary>
    /// Gets the RequestedFacets value.
    /// </summary>
    public SupervisorContextFacet RequestedFacets { get; }
    /// <summary>
    /// Gets the Limits value.
    /// </summary>
    public SupervisorContextLimits Limits { get; }

    /// <summary>
    /// Performs the ValidateAgainst contract operation.
    /// </summary>
    public void ValidateAgainst(DelegationProgress waitingProgress)
    {
        ArgumentNullException.ThrowIfNull(waitingProgress);
        if (waitingProgress.State != DelegationState.WaitingForSupervisor
            || waitingProgress.Checkpoint is null)
        {
            throw new ArgumentException("Context can only be requested for a waiting supervisor checkpoint.", nameof(waitingProgress));
        }

        if (waitingProgress.Revision < 0
            || waitingProgress.WorkerCalls < 0
            || waitingProgress.Retries < 0
            || waitingProgress.UpdatedAt == default)
        {
            throw new ArgumentException("The waiting progress snapshot contains invalid revision, counters, or timestamp.", nameof(waitingProgress));
        }

        waitingProgress.Checkpoint.Validate();
        if (waitingProgress.Checkpoint.ExpectedObservableRevision != waitingProgress.Revision
            || waitingProgress.Checkpoint.DelegationId != waitingProgress.DelegationId)
        {
            throw new ArgumentException("The waiting progress checkpoint must match its delegation and revision.", nameof(waitingProgress));
        }

        if (waitingProgress.DelegationId != DelegationId
            || waitingProgress.Checkpoint.CheckpointId != CheckpointId
            || waitingProgress.Revision != ExpectedRevision)
        {
            throw new ArgumentException("Context request must match the current delegation, checkpoint, and observable revision.", nameof(waitingProgress));
        }
    }
}

/// <summary>A bounded human-readable status or summary item; never raw transcript content.</summary>
public sealed record SupervisorContextItem
{
    /// <summary>
    /// Initializes a new instance of the SupervisorContextItem type.
    /// </summary>
    public SupervisorContextItem(SupervisorContextFacet facet, string key, string text)
    {
        ContextContracts.RequireSingleFacet(facet, nameof(facet));
        if (facet is not (SupervisorContextFacet.Status or SupervisorContextFacet.Summary))
        {
            throw new ArgumentException("Context items must be status or summary facets.", nameof(facet));
        }

        Facet = facet;
        Key = ContextContracts.Identity(key, nameof(key), 256);
        Text = ContextContracts.Prose(text, nameof(text), 16_384);
    }

    /// <summary>
    /// Gets the Facet value.
    /// </summary>
    public SupervisorContextFacet Facet { get; }
    /// <summary>
    /// Gets the Key value.
    /// </summary>
    public string Key { get; }
    /// <summary>
    /// Gets the Text value.
    /// </summary>
    public string Text { get; }
    /// <summary>
    /// Gets the Utf8ByteCount value.
    /// </summary>
    public int Utf8ByteCount => System.Text.Encoding.UTF8.GetByteCount(Text);
}

/// <summary>Identity used to correlate context with another durable system.</summary>
public sealed record ContextCorrelationReference
{
    /// <summary>
    /// Initializes a new instance of the ContextCorrelationReference type.
    /// </summary>
    public ContextCorrelationReference(string provider, string kind, string identifier, string? revision = null)
    {
        Provider = ContextContracts.Identity(provider, nameof(provider), 128);
        Kind = ContextContracts.Identity(kind, nameof(kind), 256);
        Identifier = ContextContracts.Identity(identifier, nameof(identifier), 1_024);
        Revision = revision is null ? null : ContextContracts.Identity(revision, nameof(revision), 512);
    }

    /// <summary>
    /// Gets the Provider value.
    /// </summary>
    public string Provider { get; }
    /// <summary>
    /// Gets the Kind value.
    /// </summary>
    public string Kind { get; }
    /// <summary>
    /// Gets the Identifier value.
    /// </summary>
    public string Identifier { get; }
    /// <summary>
    /// Gets the Revision value.
    /// </summary>
    public string? Revision { get; }
}

/// <summary>
/// Provider-neutral immutable provenance identity. It conveys reproducibility,
/// not authority or validation proof, and does not embed a primitive store.
/// </summary>
public sealed record ContextProvenanceReference
{
    /// <summary>
    /// Initializes a new instance of the ContextProvenanceReference type.
    /// </summary>
    public ContextProvenanceReference(
        string provider,
        string kind,
        string identifier,
        string? revision = null,
        string? contentHash = null)
    {
        Provider = ContextContracts.Identity(provider, nameof(provider), 128);
        Kind = ContextContracts.Identity(kind, nameof(kind), 256);
        Identifier = ContextContracts.Identity(identifier, nameof(identifier), 1_024);
        Revision = revision is null ? null : ContextContracts.Identity(revision, nameof(revision), 512);
        ContentHash = contentHash is null
            ? null
            : IdentityText.RequireSha256(contentHash, nameof(contentHash));
    }

    /// <summary>
    /// Gets the Provider value.
    /// </summary>
    public string Provider { get; }
    /// <summary>
    /// Gets the Kind value.
    /// </summary>
    public string Kind { get; }
    /// <summary>
    /// Gets the Identifier value.
    /// </summary>
    public string Identifier { get; }
    /// <summary>
    /// Gets the Revision value.
    /// </summary>
    public string? Revision { get; }
    /// <summary>
    /// Gets the ContentHash value.
    /// </summary>
    public string? ContentHash { get; }

    /// <summary>
    /// Performs the CangjieSnapshot contract operation.
    /// </summary>
    public static ContextProvenanceReference CangjieSnapshot(
        string identifier,
        string? contentHash = null) => new("cangjie", "context-snapshot", identifier, null, contentHash);

    /// <summary>
    /// Performs the HetuIndexPublication contract operation.
    /// </summary>
    public static ContextProvenanceReference HetuIndexPublication(
        string repositoryIdentifier,
        string indexRunIdentifier,
        string? indexIdentity = null) => new("hetu", "index-publication", repositoryIdentifier, indexRunIdentifier, indexIdentity);
}

/// <summary>Bounded context package for one exact waiting checkpoint.</summary>
public sealed record SupervisorContextPackage
{
    /// <summary>
    /// Initializes a new instance of the SupervisorContextPackage type.
    /// </summary>
    public SupervisorContextPackage(
        DelegationId delegationId,
        SupervisorCheckpointId checkpointId,
        long revision,
        SupervisorContextFacet requestedFacets,
        SupervisorContextLimits appliedLimits,
        IReadOnlyList<SupervisorContextItem> items,
        IReadOnlyList<DelegationArtifactReference> artifacts,
        IReadOnlyList<ContextCorrelationReference> correlations,
        IReadOnlyList<ContextProvenanceReference> primitiveReferences,
        IReadOnlyList<ContextFacetOutcome> facetOutcomes)
    {
        ContextContracts.RequireDelegation(delegationId, nameof(delegationId));
        checkpointId.Validate();
        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        ContextContracts.RequireFacets(requestedFacets, nameof(requestedFacets));
        ArgumentNullException.ThrowIfNull(appliedLimits);
        Items = Snapshot(items, nameof(items));
        Artifacts = Snapshot(artifacts, nameof(artifacts));
        Correlations = Snapshot(correlations, nameof(correlations));
        PrimitiveReferences = Snapshot(primitiveReferences, nameof(primitiveReferences));
        FacetOutcomes = Snapshot(facetOutcomes, nameof(facetOutcomes));
        foreach (var item in Items) ArgumentNullException.ThrowIfNull(item);
        foreach (var artifact in Artifacts) ContextContracts.ValidateArtifact(artifact, delegationId);
        foreach (var correlation in Correlations) ArgumentNullException.ThrowIfNull(correlation);
        foreach (var reference in PrimitiveReferences) ArgumentNullException.ThrowIfNull(reference);
        foreach (var outcome in FacetOutcomes) ArgumentNullException.ThrowIfNull(outcome);

        if (Items.Count + Artifacts.Count + Correlations.Count + PrimitiveReferences.Count > appliedLimits.MaxItems)
        {
            throw new ArgumentException("The context package exceeds its applied item limit.", nameof(appliedLimits));
        }

        var bytes = Items.Sum(item => item.Utf8ByteCount);
        if (bytes > appliedLimits.MaxInlineSummaryBytes)
        {
            throw new ArgumentException("The context package exceeds its applied UTF-8 summary limit.", nameof(appliedLimits));
        }

        DelegationId = delegationId;
        CheckpointId = checkpointId;
        Revision = revision;
        RequestedFacets = requestedFacets;
        AppliedLimits = appliedLimits;
        InlineSummaryUtf8Bytes = bytes;
        ValidateFacetConsistency();
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
    /// Gets the Revision value.
    /// </summary>
    public long Revision { get; }
    /// <summary>
    /// Gets the RequestedFacets value.
    /// </summary>
    public SupervisorContextFacet RequestedFacets { get; }
    /// <summary>
    /// Gets the AppliedLimits value.
    /// </summary>
    public SupervisorContextLimits AppliedLimits { get; }
    /// <summary>
    /// Gets the Items value.
    /// </summary>
    public IReadOnlyList<SupervisorContextItem> Items { get; }
    /// <summary>
    /// Gets the Artifacts value.
    /// </summary>
    public IReadOnlyList<DelegationArtifactReference> Artifacts { get; }
    /// <summary>
    /// Gets the Correlations value.
    /// </summary>
    public IReadOnlyList<ContextCorrelationReference> Correlations { get; }
    /// <summary>
    /// Gets the PrimitiveReferences value.
    /// </summary>
    public IReadOnlyList<ContextProvenanceReference> PrimitiveReferences { get; }
    /// <summary>
    /// Gets the FacetOutcomes value.
    /// </summary>
    public IReadOnlyList<ContextFacetOutcome> FacetOutcomes { get; }
    /// <summary>
    /// Gets the InlineSummaryUtf8Bytes value.
    /// </summary>
    public int InlineSummaryUtf8Bytes { get; }

    /// <summary>
    /// Performs the ValidateAgainst contract operation.
    /// </summary>
    public void ValidateAgainst(SupervisorContextRequest request, DelegationProgress waitingProgress)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.ValidateAgainst(waitingProgress);
        if (DelegationId != request.DelegationId
            || CheckpointId != request.CheckpointId
            || Revision != request.ExpectedRevision)
        {
            throw new ArgumentException("Context package is not bound to the requested checkpoint fence.", nameof(request));
        }

        if (RequestedFacets != request.RequestedFacets)
        {
            throw new ArgumentException("Context package facets do not match the request.", nameof(request));
        }

        if (AppliedLimits.MaxItems > request.Limits.MaxItems
            || AppliedLimits.MaxInlineSummaryBytes > request.Limits.MaxInlineSummaryBytes)
        {
            throw new ArgumentException("Applied context limits cannot exceed the requested limits.", nameof(request));
        }

        ValidateFacetConsistency();
        foreach (var artifact in Artifacts)
        {
            ContextContracts.ValidateArtifact(artifact, request.DelegationId);
        }
    }

    private static IReadOnlyList<T> Snapshot<T>(IReadOnlyList<T> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (values.Count > ContextContracts.MaximumItems)
        {
            throw new ArgumentException($"A context collection cannot exceed {ContextContracts.MaximumItems} items.", parameterName);
        }

        return Array.AsReadOnly(values.ToArray());
    }

    private void ValidateFacetConsistency()
    {
        var requested = ContextContracts.EnumerateFacets(RequestedFacets).ToArray();
        var outcomes = FacetOutcomes.ToDictionary(outcome => outcome.Facet);
        if (outcomes.Count != FacetOutcomes.Count || outcomes.Count != requested.Length)
        {
            throw new ArgumentException("Context must contain exactly one outcome for every requested facet.", nameof(FacetOutcomes));
        }

        if (Items.Any(item => !requested.Contains(item.Facet))
            || (Artifacts.Count > 0 && !requested.Contains(SupervisorContextFacet.Artifacts))
            || (Correlations.Count > 0 && !requested.Contains(SupervisorContextFacet.Correlations))
            || (PrimitiveReferences.Count > 0 && !requested.Contains(SupervisorContextFacet.PrimitiveReferences)))
        {
            throw new ArgumentException("Context contains content for an unrequested facet.", nameof(RequestedFacets));
        }

        foreach (var outcome in FacetOutcomes)
        {
            var actualCount = outcome.Facet switch
            {
                SupervisorContextFacet.Status or SupervisorContextFacet.Summary => Items.Count(item => item.Facet == outcome.Facet),
                SupervisorContextFacet.Artifacts => Artifacts.Count,
                SupervisorContextFacet.Correlations => Correlations.Count,
                SupervisorContextFacet.PrimitiveReferences => PrimitiveReferences.Count,
                _ => throw new ArgumentOutOfRangeException(nameof(outcome), "Unknown context facet."),
            };

            if (!requested.Contains(outcome.Facet) || outcome.ItemCount != actualCount)
            {
                throw new ArgumentException("Context facet outcomes do not match requested facets and content.", nameof(FacetOutcomes));
            }
        }
    }
}

/// <summary>
/// Resolves authoritative waiting state, applies host authorization/redaction,
/// and returns bounded context. Implementations must not include secrets, raw
/// prompts, full conversations, or repository contents.
/// </summary>
public interface ISupervisorContextProvider
{
    /// <summary>
    /// Performs the GetAsync contract operation.
    /// </summary>
    ValueTask<SupervisorContextPackage> GetAsync(
        SupervisorIdentity supervisor,
        SupervisorContextRequest request,
        CancellationToken cancellationToken = default);
}

internal static class ContextContracts
{
    /// <summary>
    /// Provides the MaximumItems contract constant.
    /// </summary>
    public const int MaximumItems = 128;
    /// <summary>
    /// Provides the MaximumInlineSummaryBytes contract constant.
    /// </summary>
    public const int MaximumInlineSummaryBytes = 65_536;

    /// <summary>
    /// Performs the Identity contract operation.
    /// </summary>
    public static string Identity(string? value, string parameterName, int maximumLength) => IdentityText.Require(value, parameterName, maximumLength);
    /// <summary>
    /// Performs the Prose contract operation.
    /// </summary>
    public static string Prose(string? value, string parameterName, int maximumLength) => IdentityText.RequireProse(value, parameterName, maximumLength);
    /// <summary>
    /// Performs the RequireDelegation contract operation.
    /// </summary>
    public static void RequireDelegation(DelegationId value, string parameterName) => IdentityText.RequireNonEmpty(value.Value, parameterName);

    /// <summary>
    /// Performs the ValidateArtifact contract operation.
    /// </summary>
    public static void ValidateArtifact(DelegationArtifactReference value, DelegationId? expectedDelegation = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArtifactContracts.ValidateArtifact(value, expectedDelegation ?? value.DelegationId);
    }

    /// <summary>
    /// Performs the RequireFacets contract operation.
    /// </summary>
    public static void RequireFacets(SupervisorContextFacet value, string parameterName)
    {
        const SupervisorContextFacet all = SupervisorContextFacet.Status | SupervisorContextFacet.Summary | SupervisorContextFacet.Artifacts | SupervisorContextFacet.Correlations | SupervisorContextFacet.PrimitiveReferences;
        if (value == SupervisorContextFacet.None || (value & ~all) != 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "At least one known context facet is required.");
        }
    }

    /// <summary>
    /// Performs the RequireSingleFacet contract operation.
    /// </summary>
    public static void RequireSingleFacet(SupervisorContextFacet value, string parameterName)
    {
        RequireFacets(value, parameterName);
        var bits = (int)value;
        if ((bits & (bits - 1)) != 0)
        {
            throw new ArgumentException("Exactly one context facet is required.", parameterName);
        }
    }

    /// <summary>
    /// Performs the EnumerateFacets contract operation.
    /// </summary>
    public static IEnumerable<SupervisorContextFacet> EnumerateFacets(SupervisorContextFacet value)
    {
        foreach (var facet in new[] { SupervisorContextFacet.Status, SupervisorContextFacet.Summary, SupervisorContextFacet.Artifacts, SupervisorContextFacet.Correlations, SupervisorContextFacet.PrimitiveReferences })
        {
            if ((value & facet) != 0) yield return facet;
        }
    }
}
