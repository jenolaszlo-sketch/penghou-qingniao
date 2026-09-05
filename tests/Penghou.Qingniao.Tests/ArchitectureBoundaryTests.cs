using FluentAssertions;

namespace Penghou.Qingniao.Tests;

public sealed class ArchitectureBoundaryTests
{
    [Fact]
    public void PublicAssemblies_DoNotReferenceProductOrTransportFrameworks()
    {
        var assemblies = new[]
        {
            typeof(DelegationId).Assembly,
            typeof(DelegationLifecycle).Assembly,
        };

        var forbiddenPrefixes = new[]
        {
            "Marang",
            "Microsoft.AspNetCore",
            "ModelContextProtocol",
        };

        foreach (var assembly in assemblies)
        {
            assembly.GetReferencedAssemblies()
                .Select(reference => reference.Name ?? string.Empty)
                .Should()
                .NotContain(
                    name => forbiddenPrefixes.Any(
                        prefix => name.StartsWith(prefix, StringComparison.Ordinal)),
                    $"{assembly.GetName().Name} must remain independent of Marang and transport frameworks");
        }
    }

    [Fact]
    public void PublicTypes_UseOnlyQingniaoNamespaces()
    {
        var publicTypes = new[]
            {
                typeof(DelegationId).Assembly,
                typeof(DelegationLifecycle).Assembly,
            }
            .Distinct()
            .SelectMany(assembly => assembly.ExportedTypes);

        publicTypes.Should().OnlyContain(
            type => type.Namespace != null &&
                    (type.Namespace.Equals("Penghou.Qingniao", StringComparison.Ordinal) ||
                     type.Namespace.StartsWith("Penghou.Qingniao.", StringComparison.Ordinal)));
    }
}
