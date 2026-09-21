using System.Reflection;
using FluentAssertions;

namespace Penghou.Qingniao.Tests;

/// <summary>
/// Architectural tripwires for the §31 boundary: Qingniao core may verify
/// and supervise the result of one bounded delegated execution, including
/// bounded checkpoint-local correction. It must not choose the surrounding
/// workflow, select the execution provider, or decide how the larger task
/// should be replanned.
/// </summary>
public sealed class BoundaryArchitectureTests
{
    private static readonly Assembly Abstractions =
        typeof(DelegationRequest).Assembly;

    private static readonly Assembly Core =
        typeof(InMemoryProviderRegistry).Assembly;

    [Fact]
    public void Core_does_not_reference_external_Penghou_packages()
    {
        var referenced = Core.GetReferencedAssemblies().Select(name => name.Name).ToArray();

        referenced.Should().NotContain(name =>
            !string.IsNullOrEmpty(name) && (name.StartsWith("Penghou.", StringComparison.Ordinal)
                && !string.Equals(name, "Penghou.Qingniao.Abstractions", StringComparison.Ordinal)));
    }

    [Fact]
    public void Abstractions_do_not_reference_external_Penghou_packages()
    {
        var referenced = Abstractions.GetReferencedAssemblies().Select(name => name.Name).ToArray();

        referenced.Should().NotContain(name =>
            !string.IsNullOrEmpty(name) && name.StartsWith("Penghou.", StringComparison.Ordinal));
    }

    [Fact]
    public void Core_contains_no_provider_selection_or_ranking_surface()
    {
        var types = Core.GetTypes();

        types.Select(type => type.Name).Should().NotContain(name =>
            name.Contains("ProviderSelection", StringComparison.Ordinal)
            || name.Contains("ProviderMatch", StringComparison.Ordinal)
            || name.Contains("ProviderHints", StringComparison.Ordinal));

        var selectionMethods = types
            .SelectMany(type => type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(method => method.Name is "Select" or "Match")
            .Select(method => $"{method.DeclaringType!.Name}.{method.Name}")
            .ToArray();

        selectionMethods.Should().BeEmpty();
        typeof(ProviderDescriptor).GetProperty("Priority").Should().BeNull();
    }

    [Fact]
    public void Core_contains_no_workflow_plan_or_provider_specific_semantics()
    {
        var types = Core.GetTypes().Concat(Abstractions.GetTypes()).ToArray();

        types.Select(type => type.Name).Should().NotContain(name =>
            name.Contains("WorkflowPlan", StringComparison.Ordinal)
            || name.Contains("Fuwen", StringComparison.Ordinal)
            || name.Contains("Siming", StringComparison.Ordinal)
            || name.Contains("Marang", StringComparison.Ordinal)
            || name.Contains("Guihua", StringComparison.Ordinal)
            || name.Contains("Zhinu", StringComparison.Ordinal)
            || name.Contains("Baize", StringComparison.Ordinal)
            || name.Contains("Guyabano", StringComparison.Ordinal)
            || name.Contains("Cangjie", StringComparison.Ordinal)
            || name.Contains("Hetu", StringComparison.Ordinal)
            || name.Contains("Hongxian", StringComparison.Ordinal));

        var workflowMembers = types
            .SelectMany(type => type.GetMembers(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(member => member.Name.Contains("WorkflowPlan", StringComparison.Ordinal)
                || member.Name.Contains("Fuwen", StringComparison.Ordinal)
                || member.Name.Contains("Siming", StringComparison.Ordinal))
            .Select(member => $"{member.DeclaringType!.Name}.{member.Name}")
            .ToArray();

        workflowMembers.Should().BeEmpty();
    }

    [Fact]
    public void Core_contains_no_embedded_correction_policy()
    {
        var methods = Core.GetTypes()
            .SelectMany(type => type.GetMethods(
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Select(method => method.Name)
            .ToArray();

        methods.Should().NotContain(name =>
            name.Contains("IsPass", StringComparison.Ordinal)
            || name.Contains("IsApprove", StringComparison.Ordinal)
            || name.Contains("ConfiguredEvaluatorCallCount", StringComparison.Ordinal));
    }

    [Fact]
    public void Admission_verifier_is_generic_and_fence_is_opaque()
    {
        typeof(IDelegationAdmissionVerifier).GetMethod("Verify")!.ReturnType.Should().Be(typeof(DelegationAdmissionDecision));

        var fenceMembers = typeof(DelegationAdmissionFence).GetMembers(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(member => member.Name);

        fenceMembers.Should().NotContain(name =>
            name.Contains("Workflow", StringComparison.Ordinal)
            || name.Contains("Fuwen", StringComparison.Ordinal)
            || name.Contains("Plan", StringComparison.Ordinal));
    }
}
