using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Penghou.Qingniao;

/// <summary>
/// Computes and verifies external-operation semantic fingerprints with a
/// local deterministic JSON contract. The semantic envelope is projected to
/// an explicitly ordered property sequence so CLR contract implementation
/// details cannot silently change the hash input. GUIDs use lowercase D
/// format, durations use exact TimeSpan ticks, deadlines use UTC round-trip
/// text, and absent budget/deadline values are represented as JSON null. The
/// input artifact order is preserved because it is semantic.
/// </summary>
internal sealed class LocalSemanticFingerprintVerifier : IExternalOperationSemanticFingerprintVerifier
{
    /// <summary>Computes the canonical lowercase SHA-256 fingerprint for an envelope.</summary>
    public string Compute(ExternalOperationSemanticInputEnvelope semanticInput)
    {
        ArgumentNullException.ThrowIfNull(semanticInput);
        EnsureSupportedEnvelope(semanticInput);
        var bytes = CanonicalBytes(semanticInput);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
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

        var bytes = CanonicalBytes(semanticInput);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return string.Equals(identity.SemanticFingerprint, hash, StringComparison.Ordinal);
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

    private static byte[] CanonicalBytes(ExternalOperationSemanticInputEnvelope semanticInput)
    {
        var writerBuffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(writerBuffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("schemaVersion", semanticInput.SchemaVersion);
            writer.WriteString("delegationId", GuidText(semanticInput.DelegationId.Value));
            writer.WritePropertyName("agent");
            writer.WriteStartObject();
            writer.WriteString("provider", semanticInput.Agent.Provider);
            writer.WriteString("identifier", semanticInput.Agent.Identifier);
            writer.WriteString("protocolVersion", semanticInput.Agent.ProtocolVersion);
            writer.WriteEndObject();
            writer.WriteString("capability", semanticInput.Capability);
            writer.WritePropertyName("inputArtifacts");
            writer.WriteStartArray();
            foreach (var artifact in semanticInput.InputArtifacts)
            {
                writer.WriteStartObject();
                writer.WriteString("delegationId", GuidText(artifact.DelegationId.Value));
                writer.WriteString("structuralNode", artifact.StructuralNode.Identifier);
                writer.WriteString("nodeGeneration", GuidText(artifact.NodeGeneration.Value));
                writer.WriteString("provider", artifact.Provider);
                writer.WriteString("repository", artifact.Repository);
                writer.WriteString("artifactId", artifact.ArtifactId);
                writer.WriteString("kind", artifact.Kind);
                writer.WriteNumber("schemaVersion", artifact.SchemaVersion);
                writer.WriteString("location", artifact.Location);
                writer.WritePropertyName("contentIdentity");
                writer.WriteStartObject();
                writer.WriteString("contractVersion", artifact.ContentIdentity.ContractVersion);
                writer.WriteString("hash", artifact.ContentIdentity.Hash);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("budget");
            if (semanticInput.Budget is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStartObject();
                if (semanticInput.Budget.MaximumTokens is null)
                {
                    writer.WriteNull("maximumTokens");
                }
                else
                {
                    writer.WriteNumber("maximumTokens", semanticInput.Budget.MaximumTokens.Value);
                }

                if (semanticInput.Budget.MaximumDuration is null)
                {
                    writer.WriteNull("maximumDurationTicks");
                }
                else
                {
                    writer.WriteNumber("maximumDurationTicks", semanticInput.Budget.MaximumDuration.Value.Ticks);
                }

                writer.WriteEndObject();
            }

            writer.WritePropertyName("deadline");
            if (semanticInput.Deadline is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStringValue(semanticInput.Deadline.Value.ToUniversalTime().ToString(
                    "O",
                    System.Globalization.CultureInfo.InvariantCulture));
            }

            writer.WriteEndObject();
            writer.Flush();
        }

        return writerBuffer.WrittenSpan.ToArray();
    }

    private static string GuidText(Guid value) => value.ToString("D").ToLowerInvariant();
}
