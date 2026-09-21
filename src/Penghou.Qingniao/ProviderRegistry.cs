using System.Globalization;
using System.Text;

namespace Penghou.Qingniao;

/// <summary>Identifies the outcome of resolving a caller-supplied provider identity.</summary>
public enum ProviderResolutionStatus
{
    /// <summary>The provider is registered, enabled, and satisfies the required capabilities.</summary>
    Resolved = 0,

    /// <summary>No provider is registered for the supplied identity.</summary>
    UnknownProvider = 1,

    /// <summary>The provider is registered but disabled.</summary>
    DisabledProvider = 2,

    /// <summary>The provider is registered and enabled but does not satisfy a required capability.</summary>
    IncompatibleProvider = 3,
}

/// <summary>
/// The immutable result of resolving one caller-supplied provider identity.
/// Qingniao resolves, verifies, and rejects; it never chooses between providers.
/// </summary>
public sealed class ProviderResolutionResult
{
    private ProviderResolutionResult(
        ProviderResolutionStatus status,
        string provider,
        ProviderDescriptor? descriptor,
        IReadOnlyList<string>? missingCapabilities)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown provider resolution status.");
        }

        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        if ((status == ProviderResolutionStatus.Resolved
                || status == ProviderResolutionStatus.DisabledProvider
                || status == ProviderResolutionStatus.IncompatibleProvider) != (descriptor is not null))
        {
            throw new ArgumentException("A known-provider result requires a descriptor and an unknown-provider result must not contain one.", nameof(descriptor));
        }

        if ((status == ProviderResolutionStatus.IncompatibleProvider) != (missingCapabilities is not null && missingCapabilities.Count != 0))
        {
            throw new ArgumentException("An incompatible-provider result requires missing capabilities and no other result may contain them.", nameof(missingCapabilities));
        }

        Status = status;
        Descriptor = descriptor;
        MissingCapabilities = missingCapabilities ?? Array.Empty<string>();
    }

    /// <summary>Creates a result containing the resolved provider descriptor.</summary>
    public static ProviderResolutionResult Resolved(ProviderDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return new ProviderResolutionResult(ProviderResolutionStatus.Resolved, descriptor.Provider, descriptor, null);
    }

    /// <summary>Creates an explicit result indicating that no provider is registered for the identity.</summary>
    public static ProviderResolutionResult UnknownProvider(string provider) =>
        new(ProviderResolutionStatus.UnknownProvider, RequireProviderIdentity(provider), null, null);

    /// <summary>Creates an explicit result indicating that the registered provider is disabled.</summary>
    public static ProviderResolutionResult DisabledProvider(ProviderDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return new ProviderResolutionResult(ProviderResolutionStatus.DisabledProvider, descriptor.Provider, descriptor, null);
    }

    /// <summary>Creates an explicit result indicating that the provider does not satisfy required capabilities.</summary>
    public static ProviderResolutionResult IncompatibleProvider(
        ProviderDescriptor descriptor,
        IReadOnlyList<string> missingCapabilities)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(missingCapabilities);
        if (missingCapabilities.Count == 0 || missingCapabilities.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("An incompatible-provider result requires at least one missing capability.", nameof(missingCapabilities));
        }

        return new ProviderResolutionResult(
            ProviderResolutionStatus.IncompatibleProvider,
            descriptor.Provider,
            descriptor,
            Array.AsReadOnly(missingCapabilities.ToArray()));
    }

    /// <summary>Gets the resolution outcome.</summary>
    public ProviderResolutionStatus Status { get; }

    /// <summary>Gets the exact ordinal provider identity that was resolved.</summary>
    public string Provider { get; }

    /// <summary>Gets the registered descriptor, or <see langword="null"/> when the provider is unknown.</summary>
    public ProviderDescriptor? Descriptor { get; }

    /// <summary>Gets the required capability names the provider does not satisfy; empty unless incompatible.</summary>
    public IReadOnlyList<string> MissingCapabilities { get; }

    /// <summary>Gets a value indicating whether the provider resolved.</summary>
    public bool IsResolved => Status == ProviderResolutionStatus.Resolved;

    private static string RequireProviderIdentity(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            throw new ArgumentException("A non-empty provider identity is required.", nameof(provider));
        }

        if (provider.Length > 512
            || provider.Normalize(System.Text.NormalizationForm.FormC) != provider
            || provider != provider.Trim()
            || provider.Contains('\r')
            || provider.Contains('\n'))
        {
            throw new ArgumentException("A provider identity must already be in canonical form.", nameof(provider));
        }

        return provider;
    }
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

    /// <summary>Resolves one caller-supplied provider identity against this snapshot.</summary>
    public ProviderResolutionResult Resolve(string provider, IReadOnlyList<CapabilityRequirement>? requiredCapabilities = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var descriptor = Providers.FirstOrDefault(candidate =>
            string.Equals(candidate.Provider, provider, StringComparison.Ordinal));
        if (descriptor is null)
        {
            return ProviderResolutionResult.UnknownProvider(provider);
        }

        if (!descriptor.Enabled)
        {
            return ProviderResolutionResult.DisabledProvider(descriptor);
        }

        var missing = ProviderCapabilityVerification.MissingCapabilities(descriptor, requiredCapabilities);
        return missing.Count == 0
            ? ProviderResolutionResult.Resolved(descriptor)
            : ProviderResolutionResult.IncompatibleProvider(descriptor, missing);
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

    /// <summary>
    /// Resolves one caller-supplied provider identity: verifies registration
    /// and availability, then verifies the required capabilities. Qingniao
    /// never chooses between providers; the caller supplies the identity.
    /// </summary>
    ProviderResolutionResult Resolve(string provider, IReadOnlyList<CapabilityRequirement>? requiredCapabilities = null);
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

    /// <summary>Resolves one caller-supplied provider identity against one captured registry snapshot.</summary>
    public ProviderResolutionResult Resolve(string provider, IReadOnlyList<CapabilityRequirement>? requiredCapabilities = null) =>
        GetSnapshot().Resolve(provider, requiredCapabilities);

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

/// <summary>Verifies that one registered provider satisfies caller-required capabilities.</summary>
internal static class ProviderCapabilityVerification
{
    /// <summary>Returns the required capability names the descriptor does not satisfy.</summary>
    internal static IReadOnlyList<string> MissingCapabilities(
        ProviderDescriptor descriptor,
        IReadOnlyList<CapabilityRequirement>? requiredCapabilities)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (requiredCapabilities is null || requiredCapabilities.Count == 0)
        {
            return Array.Empty<string>();
        }

        var missing = new List<string>();
        foreach (var requirement in requiredCapabilities)
        {
            ArgumentNullException.ThrowIfNull(requirement);
            var satisfied = descriptor.Capabilities.Any(capability =>
                string.Equals(capability.Name, requirement.Name, StringComparison.Ordinal)
                && capability.Version >= requirement.MinimumVersion
                && requirement.Attributes.All(pair =>
                    capability.Attributes.TryGetValue(pair.Key, out var value)
                    && string.Equals(value, pair.Value, StringComparison.Ordinal)));
            if (!satisfied)
            {
                missing.Add(requirement.Name);
            }
        }

        return Array.AsReadOnly(missing.ToArray());
    }
}
