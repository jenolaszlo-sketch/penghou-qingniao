namespace Penghou.Qingniao;

/// <summary>
/// Immutable provider inputs materialized from one admitted delegation. The
/// coordinator binds these exact values into the provider start identity, so
/// execution always corresponds to the request it accepted.
/// </summary>
public sealed record MaterializedDelegationInputs
{
    /// <summary>
    /// Initializes materialized delegation inputs.
    /// </summary>
    public MaterializedDelegationInputs(
        IReadOnlyList<DelegationArtifactReference> inputArtifacts,
        ExternalOperationBudgetHint? budget,
        DateTimeOffset? deadline,
        ExternalAgentReference agent)
    {
        ArgumentNullException.ThrowIfNull(inputArtifacts);
        if (inputArtifacts.Count > ArtifactContracts.MaximumCollectionItems)
        {
            throw new ArgumentException(
                $"Materialized inputs cannot contain more than {ArtifactContracts.MaximumCollectionItems} input artifacts.",
                nameof(inputArtifacts));
        }

        var snapshot = inputArtifacts.ToArray();
        var identities = new HashSet<(string Provider, string Repository, string ArtifactId)>();
        foreach (var artifact in snapshot)
        {
            ArgumentNullException.ThrowIfNull(artifact);
            if (!identities.Add((artifact.Provider, artifact.Repository, artifact.ArtifactId)))
            {
                throw new ArgumentException(
                    "Materialized inputs cannot contain duplicate artifact identities.",
                    nameof(inputArtifacts));
            }
        }

        ArgumentNullException.ThrowIfNull(agent);
        agent.Validate();
        InputArtifacts = Array.AsReadOnly(snapshot);
        Budget = budget;
        Deadline = deadline;
        Agent = agent;
    }

    /// <summary>Gets the immutable input artifacts the provider must consume.</summary>
    public IReadOnlyList<DelegationArtifactReference> InputArtifacts { get; }

    /// <summary>Gets the applicable provider budget hint, if any.</summary>
    public ExternalOperationBudgetHint? Budget { get; }

    /// <summary>Gets the applicable provider deadline, if any.</summary>
    public DateTimeOffset? Deadline { get; }

    /// <summary>
    /// Gets the trusted agent identity: the exact provider, logical agent
    /// name, and pinned protocol version the host authorizes. This replaces
    /// any provider-name-derived default; Qingniao binds it into the start
    /// identity but never invents it.
    /// </summary>
    public ExternalAgentReference Agent { get; }
}

/// <summary>
/// Host seam that turns an admitted objective, workspace reference, criteria
/// and context into immutable execution artifacts plus applicable bounds and
/// trusted agent metadata.
/// </summary>
/// <remarks>
/// Raw prompts and filesystem paths stay behind host policy: this seam runs
/// in host code, sees the full <see cref="DelegationRequest"/>, and returns
/// only opaque artifact identities, bounds, and agent metadata. Qingniao core
/// never interprets objective prose or resolves workspace paths.
/// Implementations must be deterministic for a given request and delegation:
/// initialization may re-invoke materialization after an interruption, and a
/// second materialization must yield the same inputs rather than duplicate
/// work. Materialization performs no side effects.
/// </remarks>
public interface IDelegationInputMaterializer
{
    /// <summary>Materializes immutable provider inputs for one admitted delegation.</summary>
    MaterializedDelegationInputs Materialize(DelegationRequest request, DelegationId delegationId);
}
