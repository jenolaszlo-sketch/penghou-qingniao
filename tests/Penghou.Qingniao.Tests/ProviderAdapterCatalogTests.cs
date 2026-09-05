using FluentAssertions;

namespace Penghou.Qingniao.Tests;

public sealed class ProviderAdapterCatalogTests
{
    [Fact]
    public void Registration_is_idempotent_only_for_the_same_adapter_instance()
    {
        var catalog = new InMemoryExternalOperationProviderCatalog();
        var descriptor = Provider("provider-a", priority: 4);
        var adapter = new StubProvider();

        var first = catalog.Register(descriptor, adapter);
        var replay = catalog.Register(Provider("provider-a", priority: 4), adapter);

        first.IsNew.Should().BeTrue();
        replay.IsNew.Should().BeFalse();
        replay.Revision.Should().Be(first.Revision);
        catalog.GetSnapshot().Revision.Should().Be(1);
        catalog.Lookup(descriptor).Adapter.Should().BeSameAs(adapter);
    }

    [Fact]
    public void Replacement_with_a_different_adapter_is_rejected_without_mutation()
    {
        var catalog = new InMemoryExternalOperationProviderCatalog();
        var descriptor = Provider("provider-a");
        var existing = new StubProvider();
        catalog.Register(descriptor, existing);

        var act = () => catalog.Register(descriptor, new StubProvider());

        act.Should().Throw<ProviderAdapterRegistrationConflictException>();
        catalog.GetSnapshot().Revision.Should().Be(1);
        catalog.Lookup(descriptor).Adapter.Should().BeSameAs(existing);
    }

    [Fact]
    public void A_descriptor_conflict_is_unauthorized_even_when_identity_matches()
    {
        var catalog = new InMemoryExternalOperationProviderCatalog();
        var adapter = new StubProvider();
        catalog.Register(Provider("provider-a", priority: 1), adapter);

        var result = catalog.Lookup(Provider("provider-a", priority: 2));

        result.Status.Should().Be(ProviderAdapterLookupStatus.Unauthorized);
        result.IsFound.Should().BeFalse();
        result.Adapter.Should().BeNull();
        catalog.GetSnapshot().Revision.Should().Be(1);
    }

    [Fact]
    public void Disabled_registered_descriptors_are_never_executable()
    {
        var catalog = new InMemoryExternalOperationProviderCatalog();
        var descriptor = Provider("provider-disabled", enabled: false);
        catalog.Register(descriptor, new StubProvider());

        var result = catalog.Lookup(descriptor);

        result.Status.Should().Be(ProviderAdapterLookupStatus.Unauthorized);
        result.Adapter.Should().BeNull();
    }

    [Fact]
    public void A_match_uses_its_descriptor_identity_and_capabilities_do_not_register_adapters()
    {
        var catalog = new InMemoryExternalOperationProviderCatalog();
        var descriptor = Provider("provider-a");
        var adapter = new StubProvider();
        catalog.Register(descriptor, adapter);
        var match = new ProviderMatch(
            descriptor,
            [new CapabilityDescriptor("different.selected.claim", 99)],
            hintScore: 0);

        catalog.Lookup(match).Adapter.Should().BeSameAs(adapter);
        catalog.Lookup(Provider("provider-unregistered")).Status
            .Should().Be(ProviderAdapterLookupStatus.Missing);
        catalog.GetSnapshot().Count.Should().Be(1);
    }

    [Fact]
    public void Snapshot_is_immutable_deterministic_and_does_not_expose_adapters()
    {
        var catalog = new InMemoryExternalOperationProviderCatalog();
        catalog.Register(Provider("provider-z"), new StubProvider());
        catalog.Register(Provider("provider-a"), new StubProvider());

        var snapshot = catalog.GetSnapshot();

        snapshot.Entries.Select(entry => entry.Provider).Should().Equal("provider-a", "provider-z");
        var mutate = () => ((IList<ProviderAdapterCatalogEntry>)snapshot.Entries)
            .Add(new ProviderAdapterCatalogEntry("provider-x", 3));
        mutate.Should().Throw<NotSupportedException>();
        typeof(ProviderAdapterCatalogEntry).GetProperty(nameof(ProviderAdapterCatalogEntry.Provider))
            .Should().NotBeNull();
        typeof(ProviderAdapterCatalogEntry).GetProperty(nameof(ProviderAdapterCatalogEntry.Revision))
            .Should().NotBeNull();
        typeof(ProviderAdapterCatalogEntry).GetProperties()
            .Select(property => property.Name)
            .Should().NotContain(nameof(ProviderAdapterLookupResult.Adapter));
        typeof(InMemoryExternalOperationProviderCatalog).IsNotPublic.Should().BeTrue();
    }

    [Fact]
    public void Replacing_the_catalog_revokes_the_old_in_memory_authorization()
    {
        var descriptor = Provider("provider-a");
        var oldCatalog = new InMemoryExternalOperationProviderCatalog();
        oldCatalog.Register(descriptor, new StubProvider());

        var replacement = new InMemoryExternalOperationProviderCatalog();

        replacement.Lookup(descriptor).Status.Should().Be(ProviderAdapterLookupStatus.Missing);
    }

    [Fact]
    public async Task Concurrent_registration_and_lookup_preserve_one_adapter_per_identity()
    {
        var catalog = new InMemoryExternalOperationProviderCatalog();
        var descriptor = Provider("provider-a");
        var adapter = new StubProvider();
        var registrations = Enumerable.Range(0, 64)
            .Select(_ => Task.Run(() => catalog.Register(descriptor, adapter)))
            .ToArray();

        var results = await Task.WhenAll(registrations);

        results.Count(result => result.IsNew).Should().Be(1);
        results.Select(result => result.Revision).Distinct().Should().Equal(1);
        catalog.GetSnapshot().Entries.Should().ContainSingle();
        catalog.Lookup(descriptor).Adapter.Should().BeSameAs(adapter);
    }

    [Fact]
    public void Capacity_is_enforced_without_partial_registration()
    {
        var catalog = new InMemoryExternalOperationProviderCatalog(2);
        catalog.Register(Provider("provider-a"), new StubProvider());
        catalog.Register(Provider("provider-b"), new StubProvider());

        var act = () => catalog.Register(Provider("provider-c"), new StubProvider());

        act.Should().Throw<InvalidOperationException>();
        catalog.GetSnapshot().Entries.Select(entry => entry.Provider)
            .Should().Equal("provider-a", "provider-b");
        catalog.GetSnapshot().Revision.Should().Be(2);
    }

    private static ProviderDescriptor Provider(string name, int priority = 0, bool enabled = true) =>
        new(name, [new CapabilityDescriptor("agent.execute", 1)], priority, enabled);

    private sealed class StubProvider : IExternalOperationProvider
    {
        public ValueTask<ExternalOperationStartReceipt> StartAsync(
            ExternalOperationStartRequest request,
            IExternalOperationHandleCaptureSink handleSink,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<ExternalOperationObservation> ObserveAsync(
            ExternalOperationHandle handle,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<ExternalOperationResult> GetResultAsync(
            ExternalOperationHandle handle,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<ExternalOperationCancellationReceipt> CancelAsync(
            ExternalOperationCancelRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<ExternalOperationResumeReceipt> ResumeAsync(
            ExternalOperationResumeRequest request,
            IExternalOperationHandleCaptureSink handleSink,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
