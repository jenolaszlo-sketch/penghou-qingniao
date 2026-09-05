namespace Penghou.Qingniao;

/// <summary>Describes the outcome of an executable provider-adapter lookup.</summary>
internal enum ProviderAdapterLookupStatus
{
    /// <summary>The requested provider identity is registered and executable.</summary>
    Found = 0,

    /// <summary>No executable adapter was registered for the requested identity.</summary>
    Missing = 1,

    /// <summary>The identity is registered, but the supplied descriptor is not the registered descriptor.</summary>
    Unauthorized = 2,
}

/// <summary>
/// An explicit executable provider-adapter lookup result. A caller must inspect
/// <see cref="Status"/> before using <see cref="Adapter"/>; a missing or
/// unauthorized result never authorizes an adapter.
/// </summary>
internal sealed class ProviderAdapterLookupResult
{
    private ProviderAdapterLookupResult(
        ProviderAdapterLookupStatus status,
        string provider,
        IExternalOperationProvider? adapter)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown provider-adapter lookup status.");
        }

        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        if ((status == ProviderAdapterLookupStatus.Found) != (adapter is not null))
        {
            throw new ArgumentException("A found result requires an adapter and a non-found result must not contain one.", nameof(adapter));
        }

        Status = status;
        Adapter = adapter;
    }

    /// <summary>Creates a successful lookup result.</summary>
    internal static ProviderAdapterLookupResult Found(
        string provider,
        IExternalOperationProvider adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        return new ProviderAdapterLookupResult(
            ProviderAdapterLookupStatus.Found,
            RequireProviderIdentity(provider),
            adapter);
    }

    /// <summary>Creates a result for an identity with no registered adapter.</summary>
    internal static ProviderAdapterLookupResult Missing(string provider) =>
        new(ProviderAdapterLookupStatus.Missing, RequireProviderIdentity(provider), null);

    /// <summary>Creates a result for a descriptor that is not host-authorized for the identity.</summary>
    internal static ProviderAdapterLookupResult Unauthorized(string provider) =>
        new(ProviderAdapterLookupStatus.Unauthorized, RequireProviderIdentity(provider), null);

    /// <summary>Gets the lookup outcome.</summary>
    public ProviderAdapterLookupStatus Status { get; }

    /// <summary>Gets the exact ordinal provider identity that was looked up.</summary>
    public string Provider { get; }

    /// <summary>
    /// Gets the registered executable adapter when <see cref="Status"/> is
    /// <see cref="ProviderAdapterLookupStatus.Found"/>; otherwise <see langword="null"/>.
    /// </summary>
    public IExternalOperationProvider? Adapter { get; }

    /// <summary>Gets a value indicating whether this result contains an executable adapter.</summary>
    public bool IsFound => Status == ProviderAdapterLookupStatus.Found;

    private static string RequireProviderIdentity(string provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (provider.Length is 0 or > 512)
        {
            throw new ArgumentException("A provider identity must contain 1 to 512 characters.", nameof(provider));
        }

        return provider;
    }
}

/// <summary>Raised when an executable adapter identity cannot be registered.</summary>
internal sealed class ProviderAdapterRegistrationConflictException : InvalidOperationException
{
    /// <summary>Initializes a conflict for one provider identity.</summary>
    public ProviderAdapterRegistrationConflictException(
        string provider,
        ProviderDescriptor existing,
        ProviderDescriptor supplied)
        : base(CreateMessage(provider))
    {
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        Existing = existing ?? throw new ArgumentNullException(nameof(existing));
        Supplied = supplied ?? throw new ArgumentNullException(nameof(supplied));
    }

    /// <summary>Gets the conflicting ordinal provider identity.</summary>
    public string Provider { get; }

    /// <summary>Gets the descriptor already registered for the identity.</summary>
    public ProviderDescriptor Existing { get; }

    /// <summary>Gets the descriptor supplied by the conflicting registration.</summary>
    public ProviderDescriptor Supplied { get; }

    private static string CreateMessage(string provider) =>
        $"An executable adapter is already registered for provider '{provider}'.";
}

/// <summary>
/// Safe metadata for one executable provider-adapter registration. The
/// executable adapter and descriptor are intentionally absent from this type.
/// </summary>
internal sealed class ProviderAdapterCatalogEntry
{
    internal ProviderAdapterCatalogEntry(string provider, long revision)
    {
        Provider = provider;
        Revision = revision;
    }

    /// <summary>Gets the exact ordinal provider identity.</summary>
    public string Provider { get; }

    /// <summary>Gets the immutable revision assigned when the adapter was added.</summary>
    public long Revision { get; }
}

/// <summary>Describes the result of adding or replaying an executable adapter.</summary>
internal sealed class ProviderAdapterRegistration
{
    internal ProviderAdapterRegistration(string provider, bool isNew, long revision)
    {
        Provider = provider;
        IsNew = isNew;
        Revision = revision;
    }

    /// <summary>Gets the exact ordinal provider identity.</summary>
    public string Provider { get; }

    /// <summary>Gets a value indicating whether this call added the adapter.</summary>
    public bool IsNew { get; }

    /// <summary>Gets the immutable revision assigned when the adapter was first added.</summary>
    public long Revision { get; }
}

/// <summary>Immutable, deterministic point-in-time view of executable adapters.</summary>
internal sealed class ProviderAdapterCatalogSnapshot
{
    internal ProviderAdapterCatalogSnapshot(
        IReadOnlyList<ProviderAdapterCatalogEntry> entries,
        long revision)
    {
        Entries = entries;
        Revision = revision;
    }

    /// <summary>Gets entries in ordinal provider-identity order.</summary>
    public IReadOnlyList<ProviderAdapterCatalogEntry> Entries { get; }

    /// <summary>Gets the catalog revision represented by this snapshot.</summary>
    public long Revision { get; }

    /// <summary>Gets the number of executable adapters in this snapshot.</summary>
    public int Count => Entries.Count;
}

/// <summary>
/// Thread-safe, bounded catalog of host-authorized executable provider
/// adapters. Registration is the authorization boundary: capability metadata
/// and provider selection never add an executable adapter. Registrations are
/// immutable; an equivalent descriptor replay succeeds only when it supplies
/// the same adapter object, while replacement and descriptor conflicts fail.
/// A coordinator must resolve the exact <see cref="ProviderMatch"/> selected
/// from one captured <see cref="ProviderRegistrySnapshot"/>; callers must not
/// construct a replacement match from capability claims. Replacing the
/// catalog instance is the revocation mechanism for this in-memory proof;
/// durable generation fencing is deferred to a later persistence boundary.
/// </summary>
internal sealed class InMemoryExternalOperationProviderCatalog
{
    /// <summary>The largest capacity accepted by this bounded catalog.</summary>
    public const int MaximumEntries = 128;

    private readonly object gate = new();
    private readonly int maximumEntries;
    private readonly Dictionary<string, RegisteredAdapter> adapters = new(StringComparer.Ordinal);
    private long revision;

    /// <summary>Initializes an empty catalog with the default maximum capacity.</summary>
    public InMemoryExternalOperationProviderCatalog()
        : this(MaximumEntries)
    {
    }

    /// <summary>Initializes an empty catalog with a bounded maximum capacity.</summary>
    /// <param name="maximumEntries">Maximum number of distinct provider identities retained.</param>
    public InMemoryExternalOperationProviderCatalog(int maximumEntries)
    {
        if (maximumEntries is < 1 or > MaximumEntries)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumEntries),
                maximumEntries,
                $"The maximum entry count must be between 1 and {MaximumEntries}.");
        }

        this.maximumEntries = maximumEntries;
    }

    /// <summary>Gets the configured maximum number of executable adapters.</summary>
    public int Capacity => maximumEntries;

    /// <summary>
    /// Registers a host-authorized descriptor and its executable adapter.
    /// </summary>
    /// <exception cref="ProviderAdapterRegistrationConflictException">
    /// Thrown when the provider identity is already bound to a different
    /// descriptor or adapter instance.
    /// </exception>
    public ProviderAdapterRegistration Register(
        ProviderDescriptor descriptor,
        IExternalOperationProvider adapter)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(adapter);

        lock (gate)
        {
            if (adapters.TryGetValue(descriptor.Provider, out var existing))
            {
                if (ReferenceEquals(existing.Adapter, adapter)
                    && ProviderDescriptorComparer.SemanticallyEqual(existing.Descriptor, descriptor))
                {
                    return new ProviderAdapterRegistration(existing.Descriptor.Provider, false, existing.Revision);
                }

                throw new ProviderAdapterRegistrationConflictException(
                    descriptor.Provider,
                    existing.Descriptor,
                    descriptor);
            }

            if (adapters.Count >= maximumEntries)
            {
                throw new InvalidOperationException(
                    $"An executable provider-adapter catalog cannot contain more than {maximumEntries} entries.");
            }

            revision = checked(revision + 1);
            adapters.Add(descriptor.Provider, new RegisteredAdapter(descriptor, adapter, revision));
            return new ProviderAdapterRegistration(descriptor.Provider, true, revision);
        }
    }

    /// <summary>
    /// Looks up an adapter for a selected provider descriptor. The descriptor
    /// must match the host-registered descriptor exactly, apart from the
    /// immutable ordering tolerated by descriptor semantics.
    /// </summary>
    public ProviderAdapterLookupResult Lookup(ProviderDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        lock (gate)
        {
            return LookupLocked(descriptor);
        }
    }

    /// <summary>
    /// Looks up an adapter for a selected provider match. Only the match's
    /// provider descriptor is used as identity; its selected capability list
    /// cannot authorize or add an adapter.
    /// </summary>
    public ProviderAdapterLookupResult Lookup(ProviderMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);
        return Lookup(match.Provider);
    }

    /// <summary>Captures an immutable snapshot of registered adapter identities.</summary>
    public ProviderAdapterCatalogSnapshot GetSnapshot()
    {
        lock (gate)
        {
            var copy = adapters.Values
                .OrderBy(adapter => adapter.Descriptor.Provider, StringComparer.Ordinal)
                .Select(adapter => new ProviderAdapterCatalogEntry(adapter.Descriptor.Provider, adapter.Revision))
                .ToArray();
            return new ProviderAdapterCatalogSnapshot(Array.AsReadOnly(copy), revision);
        }
    }

    private ProviderAdapterLookupResult LookupLocked(ProviderDescriptor descriptor)
    {
        if (!adapters.TryGetValue(descriptor.Provider, out var registered))
        {
            return ProviderAdapterLookupResult.Missing(descriptor.Provider);
        }

        if (!descriptor.Enabled || !registered.Descriptor.Enabled)
        {
            return ProviderAdapterLookupResult.Unauthorized(descriptor.Provider);
        }

        return ProviderDescriptorComparer.SemanticallyEqual(registered.Descriptor, descriptor)
            ? ProviderAdapterLookupResult.Found(registered.Descriptor.Provider, registered.Adapter)
            : ProviderAdapterLookupResult.Unauthorized(descriptor.Provider);
    }

    private sealed record RegisteredAdapter(
        ProviderDescriptor Descriptor,
        IExternalOperationProvider Adapter,
        long Revision);
}
