using FluentAssertions;

namespace Penghou.Qingniao.Tests;

public sealed class BudgetCapabilityContractsTests
{
    private static readonly DelegationId Delegation = new(Guid.Parse("00000000-0000-0000-0000-000000000001"));
    private static readonly DateTimeOffset RecordedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    [Fact]
    public void Quantities_are_integer_only_bounded_and_currency_safe()
    {
        BudgetQuantity.Tokens(12).Kind.Should().Be(BudgetQuantityKind.Tokens);
        BudgetQuantity.MinorCurrencyUnits("USD", 12).Currency.Should().Be("USD");

        var invalid = new Action[]
        {
            () => new BudgetQuantity((BudgetQuantityKind)99, 1),
            () => new BudgetQuantity(BudgetQuantityKind.Tokens, -1),
            () => new BudgetQuantity(BudgetQuantityKind.Tokens, 1, "USD"),
            () => BudgetQuantity.MinorCurrencyUnits("usd", 1),
            () => BudgetQuantity.MinorCurrencyUnits("USD", BudgetQuantity.MaximumValue + 1),
        };

        foreach (var action in invalid) action.Should().Throw<Exception>();
    }

    [Fact]
    public void Definitions_and_receipts_snapshot_and_reject_ambiguous_dimensions()
    {
        var limits = new List<BudgetLimit> { new("worker.calls", BudgetQuantity.Count(2)) };
        var definition = new BudgetDefinition(BudgetDefinition.CurrentVersion, limits);
        limits.Add(new BudgetLimit("usage.tokens", BudgetQuantity.Tokens(10)));

        definition.Limits.Should().ContainSingle();
        var charges = new List<BudgetCharge> { new("worker.calls", BudgetQuantity.Count(1)) };
        var receipt = Receipt(charges);
        charges.Clear();
        receipt.Charges.Should().ContainSingle();

        var duplicate = () => new BudgetConsumptionReceipt(
            Delegation,
            Guid.NewGuid(),
            definition.Version,
            1,
            RecordedAt,
            [new BudgetCharge("worker.calls", BudgetQuantity.Count(1)), new BudgetCharge("worker.calls", BudgetQuantity.Count(1))]);
        duplicate.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Accounting_is_ordered_checked_and_reports_durable_exhaustion()
    {
        var definition = new BudgetDefinition(BudgetDefinition.CurrentVersion,
            [new BudgetLimit("worker.calls", BudgetQuantity.Count(2)), new BudgetLimit("usage.tokens", BudgetQuantity.Tokens(10))]);
        var empty = BudgetAccounting.Empty(definition, Delegation);
        var first = BudgetAccounting.Apply(definition, empty, Receipt([new BudgetCharge("worker.calls", BudgetQuantity.Count(1))]));
        first.Accepted.Should().BeTrue();
        var exceeded = BudgetAccounting.Apply(definition, first.Snapshot, Receipt([new BudgetCharge("worker.calls", BudgetQuantity.Count(2))], sequence: 2));

        exceeded.Accepted.Should().BeFalse();
        exceeded.Exceeded.Should().NotBeNull();
        exceeded.Exceeded!.Consumed.Value.Should().Be(3);
        exceeded.Exceeded.Limit.Value.Should().Be(2);
        exceeded.Snapshot.Charges.Single().Amount.Value.Should().Be(3);

        var replay = () => BudgetAccounting.Apply(definition, first.Snapshot, Receipt([new BudgetCharge("worker.calls", BudgetQuantity.Count(1))], sequence: 1));
        replay.Should().Throw<InvalidOperationException>();
        var duplicateWithNewSequence = () => BudgetAccounting.Apply(
            definition,
            first.Snapshot,
            Receipt([new BudgetCharge("worker.calls", BudgetQuantity.Count(1))], sequence: 2, receiptId: first.Snapshot.ReceiptIds[0]));
        duplicateWithNewSequence.Should().Throw<InvalidOperationException>();
        var unknown = () => BudgetAccounting.Apply(definition, empty, Receipt([new BudgetCharge("unknown", BudgetQuantity.Count(1))]));
        unknown.Should().Throw<ArgumentException>();

        var otherDelegation = new DelegationId(Guid.Parse("00000000-0000-0000-0000-000000000002"));
        var crossDelegation = () => BudgetAccounting.Apply(
            definition,
            empty,
            Receipt([new BudgetCharge("worker.calls", BudgetQuantity.Count(1))], delegationId: otherDelegation));
        crossDelegation.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Accounting_prevalidates_all_dimensions_before_aggregation()
    {
        var definition = new BudgetDefinition(
            BudgetDefinition.CurrentVersion,
            [new BudgetLimit("worker.calls", BudgetQuantity.Count(2))]);
        var receipt = Receipt(
            [
                new BudgetCharge("worker.calls", BudgetQuantity.Count(1)),
                new BudgetCharge("not-defined", BudgetQuantity.Count(1)),
            ]);

        var apply = () => BudgetAccounting.Apply(definition, BudgetAccounting.Empty(definition, Delegation), receipt);
        apply.Should().Throw<ArgumentException>().WithMessage("*undefined budget dimension*");
    }

    [Fact]
    public void Budget_exceeded_terminal_results_require_owned_evidence_and_replay_it()
    {
        var receipt = Receipt([new BudgetCharge("worker.calls", BudgetQuantity.Count(2))]);
        var outcome = new BudgetExceededOutcome(
            Delegation,
            BudgetDefinition.CurrentVersion,
            receipt.Charges[0],
            BudgetQuantity.Count(1),
            BudgetQuantity.Count(2),
            receipt.ReceiptId,
            "worker call ceiling reached",
            RecordedAt);
        var result = new DelegationResult(Delegation, DelegationState.BudgetExceeded, "budget exhausted", Evidence(), [], [], RecordedAt, budgetExceeded: outcome);

        DelegationLifecycle.ValidateResult(result);
        DelegationLifecycle.ValidateResultPublication(result, new DelegationResult(Delegation, DelegationState.BudgetExceeded, "budget exhausted", Evidence(), [], [], RecordedAt, budgetExceeded: outcome));

        var missing = new DelegationResult(Delegation, DelegationState.BudgetExceeded, "budget exhausted", Evidence(), [], [], RecordedAt);
        var invalid = () => DelegationLifecycle.ValidateResult(missing);
        invalid.Should().Throw<DelegationLifecycleViolationException>();

        var mismatchedQuantity = () => new BudgetExceededOutcome(
            Delegation,
            BudgetDefinition.CurrentVersion,
            receipt.Charges[0],
            BudgetQuantity.Tokens(1),
            BudgetQuantity.Tokens(2),
            receipt.ReceiptId,
            "wrong unit",
            RecordedAt);
        mismatchedQuantity.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Preflight_budget_decision_keeps_actual_usage_and_refused_charge_distinct()
    {
        var decision = new BudgetExceededOutcome(
            Delegation,
            BudgetDefinition.CurrentVersion,
            new BudgetCharge("worker.calls", BudgetQuantity.Count(1)),
            BudgetQuantity.Count(5),
            BudgetQuantity.Count(6),
            BudgetQuantity.Count(5),
            new BudgetCharge("worker.calls", BudgetQuantity.Count(1)),
            Guid.NewGuid(),
            null,
            "the next worker call was refused",
            RecordedAt);

        decision.ActualConsumed.Value.Should().Be(5);
        decision.RefusedCharge!.Amount.Value.Should().Be(1);
        decision.Consumed.Value.Should().Be(6);
        decision.TriggeringReceiptId.Should().BeNull();

        var wrongDimension = () => new BudgetExceededOutcome(
            Delegation,
            BudgetDefinition.CurrentVersion,
            new BudgetCharge("worker.calls", BudgetQuantity.Count(1)),
            BudgetQuantity.Count(5),
            BudgetQuantity.Count(6),
            BudgetQuantity.Count(5),
            new BudgetCharge("retries", BudgetQuantity.Count(1)),
            Guid.NewGuid(),
            null,
            "wrong refused dimension",
            RecordedAt);
        wrongDimension.Should().Throw<ArgumentException>();

        var falsePostCharge = () => new BudgetExceededOutcome(
            Delegation,
            BudgetDefinition.CurrentVersion,
            new BudgetCharge("worker.calls", BudgetQuantity.Count(1)),
            BudgetQuantity.Count(5),
            BudgetQuantity.Count(6),
            BudgetQuantity.Count(5),
            null,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "actual total does not match",
            RecordedAt);
        falsePostCharge.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Provider_matching_is_open_and_deterministic()
    {
        var request = new ProviderSelectionRequest(
            [new CapabilityRequirement("agent.execute", 2, new Dictionary<string, string> { ["workspace"] = "isolated" })],
            new ProviderHints(preferredProviders: ["provider-b", "provider-a"], model: "model-v2"));
        var providers = new[]
        {
            new ProviderDescriptor("provider-a", [Capability("agent.execute", 2)], priority: 100, models: ["model-v2"]),
            new ProviderDescriptor("provider-b", [Capability("agent.execute", 3, ("isolated", "yes"))], priority: 1, models: ["model-v2"]),
            new ProviderDescriptor("provider-c", [Capability("agent.execute", 3, ("workspace", "isolated"))], priority: 100, models: ["future-model"]),
            new ProviderDescriptor("disabled", [Capability("agent.execute", 99, ("workspace", "isolated"))], priority: 1, enabled: false),
        };

        var matches = ProviderSelection.Match(request, providers);
        matches.Select(match => match.Provider.Provider).Should().Equal("provider-c");
        ProviderSelection.Select(request, providers)!.Provider.Provider.Should().Be("provider-c");

        var duplicateModels = () => new ProviderDescriptor("duplicate", [Capability("agent.execute", 2)], models: ["model-v1", "model-v1"]);
        duplicateModels.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Provider_selection_orders_by_hint_then_priority_then_ordinal_identity()
    {
        var requirement = new ProviderSelectionRequest([new CapabilityRequirement("agent.execute", 1)]);
        var providers = new[]
        {
            new ProviderDescriptor("provider-z", [Capability("agent.execute", 1)], priority: 100),
            new ProviderDescriptor("provider-b", [Capability("agent.execute", 1)], priority: 1),
            new ProviderDescriptor("provider-a", [Capability("agent.execute", 1)], priority: 1),
        };

        ProviderSelection.Match(requirement, providers).Select(match => match.Provider.Provider)
            .Should().Equal("provider-z", "provider-a", "provider-b");

        var hinted = new ProviderSelectionRequest(
            [new CapabilityRequirement("agent.execute", 1)],
            new ProviderHints(preferredProviders: ["provider-b"]));
        var matches = ProviderSelection.Match(hinted, providers);
        matches.Select(match => match.Provider.Provider).Should().Equal("provider-b", "provider-z", "provider-a");
        matches[0].HintScore.Should().BeGreaterThan(matches[1].HintScore);
    }

    [Fact]
    public void Provider_selection_requires_capability_version_and_all_attributes()
    {
        var request = new ProviderSelectionRequest(
            [new CapabilityRequirement("agent.execute", 2, new Dictionary<string, string> { ["workspace"] = "isolated" })]);
        var providers = new[]
        {
            new ProviderDescriptor("too-old", [Capability("agent.execute", 1, ("workspace", "isolated"))]),
            new ProviderDescriptor("wrong-attribute", [Capability("agent.execute", 2, ("workspace", "shared"))]),
            new ProviderDescriptor("compatible", [Capability("agent.execute", 3, ("workspace", "isolated"))]),
        };

        ProviderSelection.Match(request, providers).Select(match => match.Provider.Provider)
            .Should().Equal("compatible");
    }

    private static CapabilityDescriptor Capability(int version, (string Key, string Value)? attribute = null) =>
        Capability("agent.execute", version, attribute);

    private static CapabilityDescriptor Capability(string name, int version, (string Key, string Value)? attribute = null) =>
        new(name, version, attribute is null ? null : new Dictionary<string, string> { [attribute.Value.Key] = attribute.Value.Value });

    private static BudgetConsumptionReceipt Receipt(
        IReadOnlyList<BudgetCharge> charges,
        long sequence = 1,
        DelegationId? delegationId = null,
        Guid? receiptId = null) =>
        new(delegationId ?? Delegation, receiptId ?? Guid.NewGuid(), BudgetDefinition.CurrentVersion, sequence, RecordedAt, charges);

    private static DelegationEvidence Evidence() => new([], [], 0, 0, null, 0);
}
