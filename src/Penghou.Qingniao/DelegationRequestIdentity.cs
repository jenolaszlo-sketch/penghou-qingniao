using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Penghou.Qingniao;

/// <summary>The versioned, deterministic identity of normalized request content.</summary>
public readonly record struct DelegationRequestFingerprint
{
    /// <summary>Fingerprint format version for requests without a plan revision.</summary>
    public const string CurrentVersion = "v1";
    /// <summary>Fingerprint format version for requests bound to a plan revision.</summary>
    public const string PlanBoundVersion = "v2";

    /// <summary>Creates a validated version and lowercase hexadecimal hash pair.</summary>
    public DelegationRequestFingerprint(string version, string hash)
    {
        Version = RequireVersion(version);
        Hash = RequireHash(hash);
    }

    /// <summary>Gets the canonical fingerprint format version.</summary>
    public string Version { get; }
    /// <summary>Gets the lowercase SHA-256 hexadecimal digest.</summary>
    public string Hash { get; }

    /// <summary>Validates the stored version and digest.</summary>
    public void Validate()
    {
        RequireVersion(Version);
        RequireHash(Hash);
    }

    /// <summary>Returns the stable <c>version:hash</c> representation.</summary>
    public override string ToString() => $"{Version}:{Hash}";

    private static string RequireVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 32 || value != value.Trim()
            || !IsAsciiLowercaseLetterOrDigit(value[0])
            || value.Any(character => !IsAsciiLowercaseLetterOrDigit(character)
                && character is not ('.' or '-' or '_')))
        {
            throw new ArgumentException(
                "Fingerprint version must be a bounded lowercase ASCII label starting with a letter or digit.",
                nameof(value));
        }

        return value;
    }

    private static bool IsAsciiLowercaseLetterOrDigit(char value) =>
        value is >= 'a' and <= 'z' or >= '0' and <= '9';

    private static string RequireHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64
            || value.Any(character => !Uri.IsHexDigit(character) || char.IsUpper(character)))
        {
            throw new ArgumentException("Fingerprint hash must contain 64 lowercase hexadecimal characters.", nameof(value));
        }

        return value;
    }
}

/// <summary>
/// Normalizes delegation request content and computes its versioned SHA-256 identity.
/// Planless requests use the historical v1 contract; requests with a plan
/// revision use v2, which includes that plan identity. The caller scope and
/// <see cref="DelegationRequest.RequestKey"/> are intentionally excluded from
/// content fingerprints and are compared separately by acceptance.
/// </summary>
public static class DelegationRequestIdentity
{
    /// <summary>Computes the versioned SHA-256 identity of a validated request.</summary>
    public static DelegationRequestFingerprint Compute(DelegationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        DelegationRequestValidator.Validate(request);

        if (request.PlanRevision is not null)
        {
            return ComputeV2(request);
        }

        var json = CanonicalizeV1(request);
        var bytes = Encoding.UTF8.GetBytes(json);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new DelegationRequestFingerprint(DelegationRequestFingerprint.CurrentVersion, hash);
    }

    /// <summary>Returns the canonical JSON bytes' UTF-16 representation for diagnostics and tests.</summary>
    public static string Canonicalize(DelegationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        DelegationRequestValidator.Validate(request);

        return request.PlanRevision is null ? CanonicalizeV1(request) : CanonicalizeV2(request);
    }

    private static DelegationRequestFingerprint ComputeV2(DelegationRequest request)
    {
        var json = CanonicalizeV2(request);
        var bytes = Encoding.UTF8.GetBytes(json);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new DelegationRequestFingerprint(DelegationRequestFingerprint.PlanBoundVersion, hash);
    }

    private static string CanonicalizeV1(DelegationRequest request)
    {
        DelegationRequestValidator.Validate(request);

        var writerBuffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(writerBuffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("acceptanceCriteria");
            WriteTextList(writer, request.AcceptanceCriteria);
            writer.WritePropertyName("budget");
            writer.WriteStartObject();
            writer.WriteNumber("maximumDurationTicks", request.Budget.MaximumDuration?.Ticks ?? 0);
            writer.WriteNumber("maximumParallelWorkers", request.Budget.MaximumParallelWorkers);
            writer.WriteNumber("maximumRetries", request.Budget.MaximumRetries);
            writer.WriteNumber("maximumWorkerCalls", request.Budget.MaximumWorkerCalls);
            writer.WriteEndObject();
            writer.WritePropertyName("constraints");
            WriteTextList(writer, request.Constraints);
            writer.WriteString("objective", NormalizeText(request.Objective));
            writer.WriteNumber("strategy", (int)request.Strategy);
            writer.WritePropertyName("workspace");
            writer.WriteStartObject();
            writer.WriteString("identifier", request.Workspace.Identifier);
            if (request.Workspace.Revision is null)
            {
                writer.WriteNull("revision");
            }
            else
            {
                writer.WriteString("revision", request.Workspace.Revision);
            }

            writer.WriteString("provider", request.Workspace.Provider);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.Flush();
        }

        return Encoding.UTF8.GetString(writerBuffer.WrittenSpan);
    }

    private static string CanonicalizeV2(DelegationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        DelegationRequestValidator.Validate(request);
        ArgumentNullException.ThrowIfNull(request.PlanRevision);

        var writerBuffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(writerBuffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("planRevision");
            writer.WriteStartObject();
            writer.WriteNumber("kind", (int)request.PlanRevision.Kind);
            writer.WriteString("identifier", request.PlanRevision.Identifier);
            writer.WriteString("revision", request.PlanRevision.Revision);
            if (request.PlanRevision.CanonicalFingerprint is null)
            {
                writer.WriteNull("canonicalFingerprint");
            }
            else
            {
                writer.WriteString("canonicalFingerprint", request.PlanRevision.CanonicalFingerprint);
            }

            writer.WriteEndObject();
            writer.WritePropertyName("request");
            WriteRequestContent(writer, request);
            writer.WriteEndObject();
            writer.Flush();
        }

        return Encoding.UTF8.GetString(writerBuffer.WrittenSpan);
    }

    /// <summary>
    /// Normalization uses Unicode NFC, converts CRLF/CR to LF, and trims only
    /// leading/trailing Unicode whitespace. Internal whitespace and list order
    /// remain semantic. JSON is compact UTF-8 with explicit property order.
    /// </summary>
    public static string NormalizeText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Normalize(NormalizationForm.FormC)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
    }

    private static void WriteTextList(Utf8JsonWriter writer, IReadOnlyList<string> values)
    {
        writer.WriteStartArray();
        foreach (var value in values)
        {
            writer.WriteStringValue(NormalizeText(value));
        }

        writer.WriteEndArray();
    }

    private static void WriteRequestContent(Utf8JsonWriter writer, DelegationRequest request)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("acceptanceCriteria");
        WriteTextList(writer, request.AcceptanceCriteria);
        writer.WritePropertyName("budget");
        writer.WriteStartObject();
        writer.WriteNumber("maximumDurationTicks", request.Budget.MaximumDuration?.Ticks ?? 0);
        writer.WriteNumber("maximumParallelWorkers", request.Budget.MaximumParallelWorkers);
        writer.WriteNumber("maximumRetries", request.Budget.MaximumRetries);
        writer.WriteNumber("maximumWorkerCalls", request.Budget.MaximumWorkerCalls);
        writer.WriteEndObject();
        writer.WritePropertyName("constraints");
        WriteTextList(writer, request.Constraints);
        writer.WriteString("objective", NormalizeText(request.Objective));
        writer.WriteNumber("strategy", (int)request.Strategy);
        writer.WritePropertyName("workspace");
        writer.WriteStartObject();
        writer.WriteString("identifier", request.Workspace.Identifier);
        if (request.Workspace.Revision is null)
        {
            writer.WriteNull("revision");
        }
        else
        {
            writer.WriteString("revision", request.Workspace.Revision);
        }

        writer.WriteString("provider", request.Workspace.Provider);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }
}

/// <summary>Raised when a caller reuses a request key for different content.</summary>
public sealed class DelegationRequestKeyConflictException : InvalidOperationException
{
    /// <summary>Initializes an exception for a request key reused with different content.</summary>
    public DelegationRequestKeyConflictException(
        DelegationCallerScope caller,
        string requestKey,
        DelegationRequestFingerprint existingFingerprint,
        DelegationRequestFingerprint suppliedFingerprint)
        : base($"Request key '{requestKey}' is already bound to different content for caller scope '{caller.Identifier}'.")
    {
        Caller = caller;
        RequestKey = requestKey;
        ExistingFingerprint = existingFingerprint;
        SuppliedFingerprint = suppliedFingerprint;
    }

    /// <summary>Gets the caller scope in which the conflict occurred.</summary>
    public DelegationCallerScope Caller { get; }
    /// <summary>Gets the conflicting request key.</summary>
    public string RequestKey { get; }
    /// <summary>Gets the fingerprint already bound to the key.</summary>
    public DelegationRequestFingerprint ExistingFingerprint { get; }
    /// <summary>Gets the supplied fingerprint that conflicted with the existing binding.</summary>
    public DelegationRequestFingerprint SuppliedFingerprint { get; }
}

/// <summary>The result of an idempotent acceptance operation.</summary>
/// <summary>Result of accepting a caller-scoped request key.</summary>
/// <param name="DelegationId">Stable identifier assigned to the accepted request.</param>
/// <param name="Fingerprint">Normalized request content identity.</param>
/// <param name="IsNew">Whether this call created the binding rather than replaying it.</param>
public sealed record DelegationAcceptance(
    DelegationId DelegationId,
    DelegationRequestFingerprint Fingerprint,
    bool IsNew);

/// <summary>
/// The smallest acceptance boundary: it binds one caller-scoped key to one
/// normalized request before provider work or cost can be started.
/// </summary>
public interface IDelegationAcceptanceRegistry
{
    /// <summary>Accepts a request idempotently within the caller scope.</summary>
    ValueTask<DelegationAcceptance> AcceptAsync(
        DelegationCallerScope caller,
        DelegationRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Thread-safe in-memory acceptance registry with caller-scoped idempotency.</summary>
public sealed class InMemoryDelegationAcceptanceRegistry : IDelegationAcceptanceRegistry
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ScopedRequestKey, AcceptedRequest> _accepted = new();

    /// <summary>Returns the original acceptance for an identical replay; conflicting content is rejected.</summary>
    public ValueTask<DelegationAcceptance> AcceptAsync(
        DelegationCallerScope caller,
        DelegationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        DelegationRequestValidator.Validate(request);

        var callerScope = caller.Identifier;
        var normalizedKey = request.RequestKey;
        var fingerprint = DelegationRequestIdentity.Compute(request);
        var key = new ScopedRequestKey(callerScope, normalizedKey);
        var candidate = new AcceptedRequest(
            DelegationId: DelegationId.New(),
            Fingerprint: fingerprint);

        var existingOrAdded = _accepted.GetOrAdd(key, candidate);
        if (existingOrAdded.Fingerprint != fingerprint)
        {
            throw new DelegationRequestKeyConflictException(
                caller,
                normalizedKey,
                existingOrAdded.Fingerprint,
                fingerprint);
        }

        return ValueTask.FromResult(new DelegationAcceptance(
            existingOrAdded.DelegationId,
            existingOrAdded.Fingerprint,
            ReferenceEquals(existingOrAdded, candidate)));
    }

    private readonly record struct ScopedRequestKey(string Caller, string RequestKey);

    private sealed record AcceptedRequest(DelegationId DelegationId, DelegationRequestFingerprint Fingerprint);
}
