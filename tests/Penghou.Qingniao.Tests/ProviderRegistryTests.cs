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
            priority: 7,
            models: ["model-b", "model-a"]);

        var first = registry.Register(provider);
        registry.Register(Provider("provider-b", [Capability("agent.execute", 1)]));
        var replay = registry.Register(Provider(
            "provider-a",
            [Capability("agent.execute", 2, ("workspace", "isolated"))],
            priority: 7,
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
        var existing = Provider("provider-a", [Capability("agent.execute", 1)], priority: 1);
        registry.Register(existing);

        var conflicting = Provider("provider-a", [Capability("agent.execute", 2)], priority: 1);
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
    public void Selection_is_deterministic_and_uses_one_snapshot()
    {
        var registry = new InMemoryProviderRegistry();
        registry.Register(Provider("provider-z", [Capability("agent.execute", 1)], priority: 100));
        registry.Register(Provider("provider-b", [Capability("agent.execute", 1)], priority: 1));
        registry.Register(Provider("provider-a", [Capability("agent.execute", 1)], priority: 1));
        var request = new ProviderSelectionRequest([new CapabilityRequirement("agent.execute", 1)]);

        registry.Match(request).Select(match => match.Provider.Provider)
            .Should().Equal("provider-z", "provider-a", "provider-b");
        var selected = registry.Select(request);
        selected.IsMatch.Should().BeTrue();
        selected.Status.Should().Be(ProviderSelectionStatus.Matched);
        selected.Match!.Provider.Provider.Should().Be("provider-z");
    }

    [Fact]
    public void No_match_is_explicit_and_does_not_use_null_selection_ambiguity()
    {
        var registry = new InMemoryProviderRegistry();
        registry.Register(Provider("provider-a", [Capability("agent.execute", 1)], enabled: false));
        var request = new ProviderSelectionRequest([new CapabilityRequirement("agent.execute", 2)]);

        var result = registry.Select(request);

        result.IsMatch.Should().BeFalse();
        result.Status.Should().Be(ProviderSelectionStatus.NoCompatibleProvider);
        result.Match.Should().BeNull();
        registry.Match(request).Should().BeEmpty();
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
    public void Selection_result_factories_enforce_their_state_invariants()
    {
        var provider = Provider("provider-a", [Capability("agent.execute", 1)]);
        var match = ProviderSelection.Match(
            new ProviderSelectionRequest([new CapabilityRequirement("agent.execute", 1)]),
            [provider]).Single();

        var matched = ProviderSelectionResult.Matched(match);
        matched.IsMatch.Should().BeTrue();
        matched.Match.Should().BeSameAs(match);
        ProviderSelectionResult.NoCompatibleProvider().IsMatch.Should().BeFalse();
        var nullMatch = () => ProviderSelectionResult.Matched(null!);
        nullMatch.Should().Throw<ArgumentNullException>();
    }

    private static ProviderDescriptor Provider(
        string name,
        IReadOnlyList<CapabilityDescriptor> capabilities,
        int priority = 0,
        bool enabled = true,
        IReadOnlyList<string>? models = null) =>
        new(name, capabilities, priority, enabled, models);

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
