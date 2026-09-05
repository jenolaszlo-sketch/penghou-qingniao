using FluentAssertions;

namespace Penghou.Qingniao.Tests;

public sealed class DelegationRequestValidatorTests
{
    [Fact]
    public void Validate_accepts_a_bounded_implement_request()
    {
        var request = CreateRequest();

        var act = () => DelegationRequestValidator.Validate(request);

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_requires_an_idempotency_key()
    {
        var request = new DelegationRequest(
            " ",
            "Add deterministic cursor pagination.",
            new WorkspaceReference("local-project", "sample-workspace", "abc123"),
            ["Pagination is deterministic."],
            ["Do not change unrelated APIs."],
            new DelegationBudget(MaximumDuration: TimeSpan.FromMinutes(20)));

        var act = () => DelegationRequestValidator.Validate(request);

        act.Should().Throw<ArgumentException>()
            .WithParameterName(nameof(DelegationRequest.RequestKey));
    }

    [Fact]
    public void Budget_rejects_an_unbounded_worker_call_count_at_construction()
    {
        var act = () => new DelegationBudget(MaximumWorkerCalls: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Validate_rejects_unimplemented_strategies()
    {
        var request = new DelegationRequest(
            "change-42",
            "Add deterministic cursor pagination.",
            new WorkspaceReference("local-project", "sample-workspace", "abc123"),
            ["Pagination is deterministic."],
            ["Do not change unrelated APIs."],
            new DelegationBudget(MaximumDuration: TimeSpan.FromMinutes(20)),
            DelegationStrategy.Investigate);

        var act = () => DelegationRequestValidator.Validate(request);

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Legacy_reference_and_budget_contracts_validate_at_construction()
    {
        var workspace = new WorkspaceReference("local-project", "sample-workspace", "abc123");
        var workflow = new WorkflowReference("zhinu", "run-1");
        var budget = new DelegationBudget(MaximumWorkerCalls: 12, MaximumRetries: 3, MaximumDuration: TimeSpan.FromMinutes(20), MaximumParallelWorkers: 4);

        workspace.Provider.Should().Be("local-project");
        workflow.Identifier.Should().Be("run-1");
        budget.MaximumRetries.Should().Be(3);

        var badWorkspace = () => new WorkspaceReference(" ", "project").Validate();
        var badWorkflow = () => new WorkflowReference("provider", new string('x', 2_049)).Validate();
        var badBudget = () => new DelegationBudget(MaximumWorkerCalls: 1_000_001);
        var badDuration = () => new DelegationBudget(MaximumDuration: TimeSpan.FromDays(366));

        badWorkspace.Should().Throw<ArgumentException>();
        badWorkflow.Should().Throw<ArgumentException>();
        badBudget.Should().Throw<ArgumentOutOfRangeException>();
        badDuration.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static DelegationRequest CreateRequest() => new(
        "change-42",
        "Add deterministic cursor pagination.",
        new WorkspaceReference("local-project", "sample-workspace", "abc123"),
        ["Pagination is deterministic."],
        ["Do not change unrelated APIs."],
        new DelegationBudget(MaximumDuration: TimeSpan.FromMinutes(20)));
}
