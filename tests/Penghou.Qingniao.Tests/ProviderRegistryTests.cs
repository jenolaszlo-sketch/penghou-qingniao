using FluentAssertions;

namespace Penghou.Qingniao.Tests;

public sealed class ProviderRegistryTests
{
    [Fact]
    public void Registration_is_idempotent_for_equivalent_descriptors_and_tracks_revision()
    {
        var registry = new InMemoryProviderRegistry();
        var provider = Provider(
            "provider-a",
            [Capability("agent.execute", 2, ("workspace", "isolated"))],
            models: ["model-b", "model-a"]);

        var first = registry.Register(provider);
        registry.Register(Provider("provider-b", [Capability("agent.execute", 1)]));
        var replay = registry.Register(Provider(
            "provider-a",
            [Capability("agent.execute", 2, ("workspace", "isolated"))],
            models: ["model-a", "model-b"]));

        first.IsNew.Should().BeTrue();
        first.Revision.Should().Be(1);
        replay.IsNew.Should().BeFalse();
        replay.Revision.Should().Be(1);
        replay.Provider.Should().BeSameAs(first.Provider);
        registry.GetSnapshot().Revision.Should().Be(2);
        registry.GetSnapshot().Providers.Select(item => item.Provider).Should().Equal("provider-a", "provider-b");
    }

    [Fact]
    public void Conflicting_identity_is_rejected_without_mutating_registry()
    {
        var registry = new InMemoryProviderRegistry();
        var existing = Provider("provider-a", [Capability("agent.execute", 1)]);
        registry.Register(existing);

        var conflicting = Provider("provider-a", [Capability("agent.execute", 2)]);
        var act = () => registry.Register(conflicting);

        var exception = act.Should().Throw<ProviderRegistrationConflictException>().Which;
        exception.Provider.Should().Be("provider-a");
        exception.Existing.Should().BeSameAs(existing);
        exception.Supplied.Should().BeSameAs(conflicting);
        registry.GetSnapshot().Revision.Should().Be(1);
        registry.GetSnapshot().Providers.Should().ContainSingle().Which.Capabilities[0].Version.Should().Be(1);
    }

    [Fact]
    public void Snapshots_are_immutable_and_isolated_from_later_registrations()
    {
        var registry = new InMemoryProviderRegistry();
        registry.Register(Provider("provider-z", [Capability("agent.execute", 1)]));
        var snapshot = registry.GetSnapshot();

        registry.Register(Provider("provider-a", [Capability("agent.execute", 1)]));

        snapshot.Revision.Should().Be(1);
        snapshot.Providers.Select(provider => provider.Provider).Should().Equal("provider-z");
        registry.GetSnapshot().Providers.Select(provider => provider.Provider).Should().Equal("provider-a", "provider-z");
        var mutate = () => ((IList<ProviderDescriptor>)snapshot.Providers).Add(Provider("provider-x", [Capability("agent.execute", 1)]));
        mutate.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Resolution_returns_the_exact_supplied_identity_from_one_snapshot()
    {
        var registry = new InMemoryProviderRegistry();
        registry.Register(Provider("provider-z", [Capability("agent.execute", 1)]));
        registry.Register(Provider("provider-b", [Capability("agent.execute", 1)]));
        registry.Register(Provider("provider-a", [Capability("agent.execute", 1)]));
        var snapshot = registry.GetSnapshot();

        var resolved = snapshot.Resolve("provider-b", [new CapabilityRequirement("agent.execute", 1)]);

        resolved.IsResolved.Should().BeTrue();
        resolved.Status.Should().Be(ProviderResolutionStatus.Resolved);
        resolved.Provider.Should().Be("provider-b");
        resolved.Descriptor.Should().NotBeNull();
        resolved.Descriptor!.Provider.Should().Be("provider-b");
        resolved.MissingCapabilities.Should().BeEmpty();
    }

    [Fact]
    public void Unknown_provider_is_rejected_without_a_descriptor()
    {
        var registry = new InMemoryProviderRegistry();
        registry.Register(Provider("provider-a", [Capability("agent.execute", 1)]));

        var result = registry.Resolve("provider-unknown", [new CapabilityRequirement("agent.execute", 1)]);

        result.IsResolved.Should().BeFalse();
        result.Status.Should().Be(ProviderResolutionStatus.UnknownProvider);
        result.Provider.Should().Be("provider-unknown");
        result.Descriptor.Should().BeNull();
    }

    [Fact]
    public void Disabled_provider_is_rejected_with_its_descriptor()
    {
        var registry = new InMemoryProviderRegistry();
        registry.Register(Provider("provider-a", [Capability("agent.execute", 1)], enabled: false));

        var result = registry.Resolve("provider-a", [new CapabilityRequirement("agent.execute", 1)]);

        result.IsResolved.Should().BeFalse();
        result.Status.Should().Be(ProviderResolutionStatus.DisabledProvider);
        result.Descriptor.Should().NotBeNull();
    }

    [Fact]
    public void Incompatible_provider_lists_the_missing_capabilities()
    {
        var registry = new InMemoryProviderRegistry();
        registry.Register(Provider("provider-a", [Capability("agent.execute", 1)]));

        var result = registry.Resolve("provider-a", [new CapabilityRequirement("agent.execute", 2)]);

        result.IsResolved.Should().BeFalse();
        result.Status.Should().Be(ProviderResolutionStatus.IncompatibleProvider);
        result.Descriptor.Should().NotBeNull();
        result.MissingCapabilities.Should().Equal("agent.execute");
    }

    [Fact]
    public void Resolution_without_requirements_checks_registration_and_availability_only()
    {
        var registry = new InMemoryProviderRegistry();
        registry.Register(Provider("provider-a", [Capability("agent.execute", 1)]));

        var resolved = registry.Resolve("provider-a");

        resolved.IsResolved.Should().BeTrue();
        resolved.Status.Should().Be(ProviderResolutionStatus.Resolved);
    }

    [Fact]
    public void Registry_enforces_the_bounded_provider_set()
    {
        var registry = new InMemoryProviderRegistry();
        for (var index = 0; index < InMemoryProviderRegistry.MaximumProviders; index++)
        {
            registry.Register(Provider($"provider-{index:000}", [Capability("agent.execute", 1)]));
        }

        var act = () => registry.Register(Provider("provider-overflow", [Capability("agent.execute", 1)]));

        var replay = registry.Register(Provider("provider-127", [Capability("agent.execute", 1)]));
        replay.IsNew.Should().BeFalse();
        replay.Revision.Should().Be(InMemoryProviderRegistry.MaximumProviders);
        act.Should().Throw<InvalidOperationException>();
        var snapshot = registry.GetSnapshot();
        snapshot.Revision.Should().Be(InMemoryProviderRegistry.MaximumProviders);
        snapshot.Providers.Should().HaveCount(InMemoryProviderRegistry.MaximumProviders);
    }

    [Fact]
    public void Aggregate_utf8_and_capability_bounds_reject_without_mutation()
    {
        var registry = new InMemoryProviderRegistry();
        for (var index = 0; index < 7; index++)
        {
            registry.Register(LargeProvider($"provider-{index:00}"));
        }

        var before = registry.GetSnapshot();
        var replay = registry.Register(LargeProvider("provider-00"));
        replay.IsNew.Should().BeFalse();
        replay.Revision.Should().Be(1);
        var act = () => registry.Register(LargeProvider("provider-overflow"));

        act.Should().Throw<InvalidOperationException>();
        var after = registry.GetSnapshot();
        after.Revision.Should().Be(before.Revision);
        after.Providers.Select(provider => provider.Provider).Should().Equal(before.Providers.Select(provider => provider.Provider));
    }

    [Fact]
    public void Aggregate_capability_bound_rejects_without_mutation()
    {
        var registry = new InMemoryProviderRegistry();
        for (var index = 0; index < 16; index++)
        {
            registry.Register(Provider(
                $"provider-{index:00}",
                Enumerable.Range(0, 128)
                    .Select(capabilityIndex => Capability($"capability-{capabilityIndex:000}", 1))
                    .ToArray()));
        }

        var before = registry.GetSnapshot();
        var act = () => registry.Register(Provider(
            "provider-overflow",
            Enumerable.Range(0, 128)
                .Select(capabilityIndex => Capability($"capability-{capabilityIndex:000}", 1))
                .ToArray()));

        act.Should().Throw<InvalidOperationException>();
        var after = registry.GetSnapshot();
        after.Revision.Should().Be(before.Revision);
        after.Providers.Should().HaveCount(before.Providers.Count);
    }

    [Fact]
    public void Resolution_result_factories_enforce_their_state_invariants()
    {
        var provider = Provider("provider-a", [Capability("agent.execute", 1)]);

        var resolved = ProviderResolutionResult.Resolved(provider);
        resolved.IsResolved.Should().BeTrue();
        resolved.Descriptor.Should().BeSameAs(provider);
        resolved.MissingCapabilities.Should().BeEmpty();

        var unknown = ProviderResolutionResult.UnknownProvider("provider-unknown");
        unknown.IsResolved.Should().BeFalse();
        unknown.Status.Should().Be(ProviderResolutionStatus.UnknownProvider);
        unknown.Descriptor.Should().BeNull();

        var disabled = ProviderResolutionResult.DisabledProvider(provider);
        disabled.Status.Should().Be(ProviderResolutionStatus.DisabledProvider);

        var incompatible = ProviderResolutionResult.IncompatibleProvider(provider, ["agent.execute"]);
        incompatible.Status.Should().Be(ProviderResolutionStatus.IncompatibleProvider);
        incompatible.MissingCapabilities.Should().Equal("agent.execute");

        var nullDescriptor = () => ProviderResolutionResult.Resolved(null!);
        nullDescriptor.Should().Throw<ArgumentNullException>();
        var emptyMissing = () => ProviderResolutionResult.IncompatibleProvider(provider, []);
        emptyMissing.Should().Throw<ArgumentException>();
    }

    private static ProviderDescriptor Provider(
        string name,
        IReadOnlyList<CapabilityDescriptor> capabilities,
        bool enabled = true,
        IReadOnlyList<string>? models = null) =>
        new(name, capabilities, enabled, models);

    private static CapabilityDescriptor Capability(
        string name,
        int version,
        (string Key, string Value)? attribute = null) =>
        new(name, version, attribute is null
            ? null
            : new Dictionary<string, string> { [attribute.Value.Key] = attribute.Value.Value });

    private static ProviderDescriptor LargeProvider(string name)
    {
        var capabilities = Enumerable.Range(0, 16)
            .Select(capabilityIndex => new CapabilityDescriptor(
                $"capability-{capabilityIndex:00}",
                1,
                Enumerable.Range(0, 32)
                    .ToDictionary(
                        attributeIndex => $"attribute-{attributeIndex:00}",
                        _ => new string('x', 1_024),
                        StringComparer.Ordinal)))
            .ToArray();
        return Provider(name, capabilities);
    }
}
