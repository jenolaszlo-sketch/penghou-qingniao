using System.Globalization;
using System.Text;

namespace Penghou.Qingniao;

/// <summary>Identifies the outcome of a provider selection operation.</summary>
public enum ProviderSelectionStatus
{
    /// <summary>At least one registered provider matched the request.</summary>
    Matched = 0,

    /// <summary>No enabled registered provider matched the request.</summary>
    NoCompatibleProvider = 1,
}

/// <summary>
/// The immutable result of selecting a provider. A no-match result is
/// represented explicitly instead of using a nullable provider match.
/// </summary>
public sealed class ProviderSelectionResult
{
    private ProviderSelectionResult(ProviderSelectionStatus status, ProviderMatch? match)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown provider selection status.");
        }

        if ((status == ProviderSelectionStatus.Matched) != (match is not null))
        {
            throw new ArgumentException("A matched result requires a match and a no-match result must not contain one.", nameof(match));
        }

        Status = status;
        Match = match;
    }

    /// <summary>Creates a result containing the selected provider match.</summary>
    public static ProviderSelectionResult Matched(ProviderMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);
        return new ProviderSelectionResult(ProviderSelectionStatus.Matched, match);
    }

    /// <summary>Creates an explicit result indicating that no provider matched.</summary>
    public static ProviderSelectionResult NoCompatibleProvider() =>
        new(ProviderSelectionStatus.NoCompatibleProvider, null);

    /// <summary>Gets the selection outcome.</summary>
    public ProviderSelectionStatus Status { get; }

    /// <summary>Gets the selected match, or <see langword="null"/> when no provider matched.</summary>
    public ProviderMatch? Match { get; }

    /// <summary>Gets a value indicating whether a provider was selected.</summary>
    public bool IsMatch => Status == ProviderSelectionStatus.Matched;
}

/// <summary>The result of registering a provider descriptor.</summary>
public sealed class ProviderRegistration
{
    internal ProviderRegistration(ProviderDescriptor provider, bool isNew, long revision)
    {
        Provider = provider;
        IsNew = isNew;
        Revision = revision;
    }

    /// <summary>Gets the descriptor that is registered after the operation.</summary>
    public ProviderDescriptor Provider { get; }

    /// <summary>Gets a value indicating whether this call created the registration.</summary>
    public bool IsNew { get; }

    /// <summary>Gets the immutable revision assigned when this descriptor was first registered.</summary>
    public long Revision { get; }
}

/// <summary>
/// Raised when a provider identity is already registered with a different
/// descriptor.
/// </summary>
public sealed class ProviderRegistrationConflictException : InvalidOperationException
{
    /// <summary>Initializes a conflict for one provider identity.</summary>
    public ProviderRegistrationConflictException(
        string provider,
        ProviderDescriptor existing,
        ProviderDescriptor supplied)
        : base($"Provider '{provider}' is already registered with a different descriptor.")
    {
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        Existing = existing ?? throw new ArgumentNullException(nameof(existing));
        Supplied = supplied ?? throw new ArgumentNullException(nameof(supplied));
    }

    /// <summary>Gets the conflicting provider identity.</summary>
    public string Provider { get; }

    /// <summary>Gets the descriptor already registered for the identity.</summary>
    public ProviderDescriptor Existing { get; }

    /// <summary>Gets the descriptor supplied by the conflicting registration.</summary>
    public ProviderDescriptor Supplied { get; }
}

/// <summary>Immutable, point-in-time view of a provider registry.</summary>
public sealed class ProviderRegistrySnapshot
{
    internal ProviderRegistrySnapshot(IReadOnlyList<ProviderDescriptor> providers, long revision)
    {
        Providers = providers;
        Revision = revision;
    }

    /// <summary>Gets providers in deterministic ordinal provider-identity order.</summary>
    public IReadOnlyList<ProviderDescriptor> Providers { get; }

    /// <summary>Gets the registry revision represented by this snapshot.</summary>
    public long Revision { get; }

    /// <summary>Returns all matching providers in deterministic selection order.</summary>
    public IReadOnlyList<ProviderMatch> Match(ProviderSelectionRequest request) =>
        ProviderSelection.Match(request, Providers);

    /// <summary>Returns an explicit match or no-compatible-provider result.</summary>
    public ProviderSelectionResult Select(ProviderSelectionRequest request)
    {
        var match = ProviderSelection.Select(request, Providers);
        return match is null
            ? ProviderSelectionResult.NoCompatibleProvider()
            : ProviderSelectionResult.Matched(match);
    }
}

/// <summary>Provider-neutral registry boundary for immutable provider descriptors.</summary>
public interface IProviderRegistry
{
    /// <summary>
    /// Registers a provider, or returns the equivalent existing registration.
    /// Callers are responsible for supplying only host-authorized descriptors.
    /// </summary>
    ProviderRegistration Register(ProviderDescriptor provider);

    /// <summary>Captures an immutable point-in-time registry view.</summary>
    ProviderRegistrySnapshot GetSnapshot();

    /// <summary>Returns all providers matching the current registry snapshot.</summary>
    IReadOnlyList<ProviderMatch> Match(ProviderSelectionRequest request);

    /// <summary>Returns an explicit selected or no-compatible-provider result.</summary>
    ProviderSelectionResult Select(ProviderSelectionRequest request);
}

/// <summary>
/// Thread-safe provider registry for local hosts. Registrations are immutable:
/// an equivalent replay succeeds idempotently, while a changed descriptor for
/// the same identity is rejected. The registry does not authorize providers;
/// callers must register only descriptors authorized by their host policy.
/// </summary>
public sealed class InMemoryProviderRegistry : IProviderRegistry
{
    /// <summary>The maximum number of provider identities in one registry.</summary>
    public const int MaximumProviders = 128;

    /// <summary>The maximum aggregate deterministic UTF-8 descriptor budget.</summary>
    public const int MaximumUtf8Bytes = 4 * 1024 * 1024;

    /// <summary>The maximum aggregate number of advertised capabilities.</summary>
    public const int MaximumCapabilities = 2_048;

    /// <summary>The maximum aggregate number of capability attributes.</summary>
    public const int MaximumCapabilityAttributes = 8_192;

    private readonly object gate = new();
    private readonly Dictionary<string, RegisteredProvider> providers = new(StringComparer.Ordinal);
    private long utf8Bytes;
    private int capabilityCount;
    private int capabilityAttributeCount;
    private long revision;

    /// <summary>Registers a provider or returns the equivalent existing registration.</summary>
    /// <exception cref="ProviderRegistrationConflictException">
    /// Thrown when the identity is already bound to a different descriptor.
    /// </exception>
    public ProviderRegistration Register(ProviderDescriptor provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var footprint = ProviderDescriptorFootprint.Measure(provider);

        lock (gate)
        {
            if (providers.TryGetValue(provider.Provider, out var existing))
            {
                if (ProviderDescriptorComparer.SemanticallyEqual(existing.Descriptor, provider))
                {
                    return new ProviderRegistration(existing.Descriptor, false, existing.Revision);
                }

                throw new ProviderRegistrationConflictException(provider.Provider, existing.Descriptor, provider);
            }

            if (providers.Count >= MaximumProviders)
            {
                throw new InvalidOperationException($"A provider registry cannot contain more than {MaximumProviders} providers.");
            }

            EnsureCapacity(footprint);

            revision = checked(revision + 1);
            providers.Add(provider.Provider, new RegisteredProvider(provider, revision, footprint));
            utf8Bytes = checked(utf8Bytes + footprint.Utf8Bytes);
            capabilityCount = checked(capabilityCount + footprint.Capabilities);
            capabilityAttributeCount = checked(capabilityAttributeCount + footprint.Attributes);
            return new ProviderRegistration(provider, true, revision);
        }
    }

    /// <summary>Captures an immutable point-in-time registry view.</summary>
    public ProviderRegistrySnapshot GetSnapshot()
    {
        lock (gate)
        {
            var copy = providers.Values
                .Select(registered => registered.Descriptor)
                .OrderBy(provider => provider.Provider, StringComparer.Ordinal)
                .ToArray();
            return new ProviderRegistrySnapshot(Array.AsReadOnly(copy), revision);
        }
    }

    /// <summary>Returns all providers matching one captured registry snapshot.</summary>
    public IReadOnlyList<ProviderMatch> Match(ProviderSelectionRequest request) => GetSnapshot().Match(request);

    /// <summary>Returns an explicit selected or no-compatible-provider result.</summary>
    public ProviderSelectionResult Select(ProviderSelectionRequest request) => GetSnapshot().Select(request);

    private static void EnsureDescriptorCapacity(ProviderDescriptorFootprint footprint)
    {
        if (footprint.Utf8Bytes > MaximumUtf8Bytes)
        {
            throw new InvalidOperationException($"A provider descriptor cannot exceed the aggregate UTF-8 budget of {MaximumUtf8Bytes} bytes.");
        }
    }

    private void EnsureCapacity(ProviderDescriptorFootprint footprint)
    {
        EnsureDescriptorCapacity(footprint);
        if (utf8Bytes > MaximumUtf8Bytes - footprint.Utf8Bytes
            || capabilityCount > MaximumCapabilities - footprint.Capabilities
            || capabilityAttributeCount > MaximumCapabilityAttributes - footprint.Attributes)
        {
            throw new InvalidOperationException(
                $"Registering provider '{footprint.Provider}' would exceed the aggregate provider registry capacity.");
        }
    }

    private sealed record RegisteredProvider(
        ProviderDescriptor Descriptor,
        long Revision,
        ProviderDescriptorFootprint Footprint);
}

internal readonly record struct ProviderDescriptorFootprint(
    string Provider,
    long Utf8Bytes,
    int Capabilities,
    int Attributes)
{
    public static ProviderDescriptorFootprint Measure(ProviderDescriptor provider)
    {
        var bytes = 0L;
        var attributes = 0;
        Add(provider.Provider);
        Add(provider.Priority.ToString(CultureInfo.InvariantCulture));
        Add(provider.Enabled ? "1" : "0");

        foreach (var model in provider.Models.OrderBy(model => model, StringComparer.Ordinal))
        {
            Add(model);
        }

        foreach (var capability in provider.Capabilities.OrderBy(capability => capability.Name, StringComparer.Ordinal))
        {
            Add(capability.Name);
            Add(capability.Version.ToString(CultureInfo.InvariantCulture));
            foreach (var pair in capability.Attributes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                Add(pair.Key);
                Add(pair.Value);
                attributes = checked(attributes + 1);
            }
        }

        return new ProviderDescriptorFootprint(provider.Provider, bytes, provider.Capabilities.Count, attributes);

        void Add(string value)
        {
            bytes = checked(bytes + Encoding.UTF8.GetByteCount(value) + 1);
        }
    }
}

internal static class ProviderDescriptorComparer
{
    public static bool SemanticallyEqual(ProviderDescriptor left, ProviderDescriptor right) =>
        string.Equals(left.Provider, right.Provider, StringComparison.Ordinal)
        && left.Priority == right.Priority
        && left.Enabled == right.Enabled
        && SequenceEqual(left.Models, right.Models, StringComparer.Ordinal)
        && CapabilitiesEqual(left.Capabilities, right.Capabilities);

    private static bool CapabilitiesEqual(
        IReadOnlyList<CapabilityDescriptor> left,
        IReadOnlyList<CapabilityDescriptor> right)
    {
        if (left.Count != right.Count) return false;

        var rightByName = right.ToDictionary(capability => capability.Name, StringComparer.Ordinal);
        foreach (var capability in left)
        {
            if (!rightByName.TryGetValue(capability.Name, out var other)
                || capability.Version != other.Version
                || !DictionaryEqual(capability.Attributes, other.Attributes))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SequenceEqual(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right,
        IEqualityComparer<string> comparer)
    {
        return left.Count == right.Count
            && left.OrderBy(value => value, StringComparer.Ordinal).SequenceEqual(
                right.OrderBy(value => value, StringComparer.Ordinal), comparer);
    }

    private static bool DictionaryEqual(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right)
    {
        if (left.Count != right.Count) return false;
        foreach (var pair in left)
        {
            if (!right.TryGetValue(pair.Key, out var value)
                || !string.Equals(pair.Value, value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
