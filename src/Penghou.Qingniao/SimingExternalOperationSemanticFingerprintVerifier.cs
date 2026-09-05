using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Penghou.Siming;

namespace Penghou.Qingniao;

/// <summary>
/// Computes and verifies external-operation semantic fingerprints with
/// Penghou.Siming's canonical JSON v2 contract.
/// </summary>
/// <remarks>
/// The semantic envelope is projected to a private, explicitly named DTO so
/// changes to CLR contract implementation details cannot silently change the
/// hash input. GUIDs use lowercase D format, node generations use lowercase D
/// format, durations use exact TimeSpan ticks, deadlines use UTC round-trip
/// text, and absent budget/deadline values are represented as JSON null. The
/// input artifact order is preserved because it is semantic.
/// </remarks>
public sealed class SimingExternalOperationSemanticFingerprintVerifier : IExternalOperationSemanticFingerprintVerifier
{
    /// <summary>Computes the canonical lowercase SHA-256 fingerprint for an envelope.</summary>
    public string Compute(ExternalOperationSemanticInputEnvelope semanticInput)
    {
        ArgumentNullException.ThrowIfNull(semanticInput);
        EnsureSupportedEnvelope(semanticInput);
        return CanonicalJsonPayloadSerializerV2.ComputeSha256(Serialize(ToPayload(semanticInput)));
    }

    /// <inheritdoc />
    public bool Matches(
        ExternalOperationStartIdentity identity,
        ExternalOperationSemanticInputEnvelope semanticInput)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(semanticInput);
        identity.Validate();

        if (identity.DelegationId != semanticInput.DelegationId
            || !string.Equals(
                semanticInput.SchemaVersion,
                ExternalOperationSemanticInputEnvelope.CurrentVersion,
                StringComparison.Ordinal))
        {
            return false;
        }

        return CanonicalJsonPayloadSerializerV2.VerifySha256(
            Serialize(ToPayload(semanticInput)),
            identity.SemanticFingerprint);
    }

    private static void EnsureSupportedEnvelope(ExternalOperationSemanticInputEnvelope semanticInput)
    {
        if (!string.Equals(
                semanticInput.SchemaVersion,
                ExternalOperationSemanticInputEnvelope.CurrentVersion,
                StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"Semantic input schema '{semanticInput.SchemaVersion}' is not supported.");
        }
    }

    private static SemanticInputPayload ToPayload(ExternalOperationSemanticInputEnvelope semanticInput) =>
        new(
            semanticInput.SchemaVersion,
            GuidText(semanticInput.DelegationId.Value),
            new AgentPayload(
                semanticInput.Agent.Provider,
                semanticInput.Agent.Identifier,
                semanticInput.Agent.ProtocolVersion),
            semanticInput.Capability,
            semanticInput.InputArtifacts.Select(ToPayload).ToArray(),
            semanticInput.Budget is null
                ? null
                : new BudgetPayload(
                    semanticInput.Budget.MaximumTokens,
                    semanticInput.Budget.MaximumDuration?.Ticks),
            semanticInput.Deadline is null
                ? null
                : semanticInput.Deadline.Value
                    .ToUniversalTime()
                    .ToString("O", CultureInfo.InvariantCulture));

    private static ArtifactPayload ToPayload(DelegationArtifactReference artifact) =>
        new(
            GuidText(artifact.DelegationId.Value),
            artifact.StructuralNode.Identifier,
            GuidText(artifact.NodeGeneration.Value),
            artifact.Provider,
            artifact.Repository,
            artifact.ArtifactId,
            artifact.Kind,
            artifact.SchemaVersion,
            artifact.Location,
            new ContentIdentityPayload(
                artifact.ContentIdentity.ContractVersion,
                artifact.ContentIdentity.Hash));

    private static JsonElement Serialize(SemanticInputPayload payload) =>
        JsonSerializer.SerializeToElement(payload);

    private static string GuidText(Guid value) => value.ToString("D").ToLowerInvariant();

    private sealed record SemanticInputPayload(
        [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
        [property: JsonPropertyName("delegationId")] string DelegationId,
        [property: JsonPropertyName("agent")] AgentPayload Agent,
        [property: JsonPropertyName("capability")] string Capability,
        [property: JsonPropertyName("inputArtifacts")] IReadOnlyList<ArtifactPayload> InputArtifacts,
        [property: JsonPropertyName("budget")] BudgetPayload? Budget,
        [property: JsonPropertyName("deadline")] string? Deadline);

    private sealed record AgentPayload(
        [property: JsonPropertyName("provider")] string Provider,
        [property: JsonPropertyName("identifier")] string Identifier,
        [property: JsonPropertyName("protocolVersion")] string ProtocolVersion);

    private sealed record BudgetPayload(
        [property: JsonPropertyName("maximumTokens")] int? MaximumTokens,
        [property: JsonPropertyName("maximumDurationTicks")] long? MaximumDurationTicks);

    private sealed record ArtifactPayload(
        [property: JsonPropertyName("delegationId")] string DelegationId,
        [property: JsonPropertyName("structuralNode")] string StructuralNode,
        [property: JsonPropertyName("nodeGeneration")] string NodeGeneration,
        [property: JsonPropertyName("provider")] string Provider,
        [property: JsonPropertyName("repository")] string Repository,
        [property: JsonPropertyName("artifactId")] string ArtifactId,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("location")] string Location,
        [property: JsonPropertyName("contentIdentity")] ContentIdentityPayload ContentIdentity);

    private sealed record ContentIdentityPayload(
        [property: JsonPropertyName("contractVersion")] string ContractVersion,
        [property: JsonPropertyName("hash")] string Hash);
}
