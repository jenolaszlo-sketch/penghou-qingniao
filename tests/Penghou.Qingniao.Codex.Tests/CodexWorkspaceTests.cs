using FluentAssertions;

namespace Penghou.Qingniao.Codex.Tests;

/// <summary>
/// QH-02 containment regressions: deterministic link/case units against a
/// fake filesystem plus real temp-directory integration (symlinks where the
/// OS permits; privileged cases skip with a report instead of counting).
/// </summary>
public sealed class CodexWorkspaceTests
{
    [Fact]
    public void Rejects_escaping_symlink()
    {
        var root = FakeRoot("escape");
        var outside = FakeRoot("escape-outside");
        var fs = new FakeWorkspaceFileSystem();
        fs.Directories.Add(root);
        fs.Directories.Add(outside);
        fs.Directories.Add(Join(root, "link"));
        fs.FinalTargets[Join(root, "link")] = outside;

        var act = () => WorkspacePathResolution.ResolveAndValidate(Join(root, "link"), root, fs);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Accepts_nested_workspace_through_interior_link()
    {
        var root = FakeRoot("interior");
        var fs = new FakeWorkspaceFileSystem();
        fs.Directories.Add(root);
        fs.Directories.Add(Join(root, "real"));
        fs.Directories.Add(Join(root, "link"));
        fs.FinalTargets[Join(root, "link")] = Join(root, "real");

        var (workspace, resolvedRoot) = WorkspacePathResolution.ResolveAndValidate(
            Join(root, "link", "sub"), root, fs);

        workspace.Should().Be(Join(root, "real", "sub"));
        resolvedRoot.Should().Be(root);
    }

    [Fact]
    public void Rejects_case_variant_sibling_on_sensitive_fs()
    {
        var root = FakeRoot("case");
        var fs = new FakeWorkspaceFileSystem { CaseSensitive = true };
        fs.Directories.Add(Join(root, "Work"));
        fs.Directories.Add(Join(root, "WORK"));

        var act = () => WorkspacePathResolution.ResolveAndValidate(Join(root, "WORK"), Join(root, "Work"), fs);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rejects_case_variant_sibling_even_when_flipped_root_spelling_exists()
    {
        var parent = FakeRoot("case-collision");
        var root = Join(parent, "Work");
        var fs = new FakeWorkspaceFileSystem { CaseSensitive = true };
        fs.Directories.Add(root);
        fs.Directories.Add(Join(parent, "WORK"));
        var firstLetter = Enumerable.Range(0, root.Length).First(index => char.IsAsciiLetter(root[index]));
        var flipped = root[..firstLetter]
            + (char.IsUpper(root[firstLetter]) ? char.ToLowerInvariant(root[firstLetter]) : char.ToUpperInvariant(root[firstLetter]))
            + root[(firstLetter + 1)..];
        fs.Directories.Add(flipped);

        var act = () => WorkspacePathResolution.ResolveAndValidate(Join(parent, "WORK", "sub"), root, fs);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Accepts_case_variant_spelling_on_insensitive_fs()
    {
        var root = FakeRoot("nocase");
        var fs = new FakeWorkspaceFileSystem { CaseSensitive = false };
        fs.Directories.Add(Join(root, "Work"));

        var (workspace, _) = WorkspacePathResolution.ResolveAndValidate(
            Join(root, "WORK", "sub"), Join(root, "Work"), fs);

        // The host spelling is preserved; containment held case-insensitively.
        workspace.Should().Be(Join(root, "WORK", "sub"));
    }

    [Fact]
    public void Refuses_when_resolution_fails()
    {
        var root = FakeRoot("unresolvable");
        var fs = new FakeWorkspaceFileSystem { ThrowOnResolve = true };
        fs.Directories.Add(root);

        var act = () => WorkspacePathResolution.ResolveAndValidate(Join(root, "sub"), root, fs);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Dangling_link_escape_rejected()
    {
        var root = FakeRoot("dangling");
        var outside = FakeRoot("dangling-outside");
        var fs = new FakeWorkspaceFileSystem();
        fs.Directories.Add(root);
        fs.LinkTargets[Join(root, "dangling")] = outside;

        var act = () => WorkspacePathResolution.ResolveAndValidate(Join(root, "dangling"), root, fs);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Root_itself_is_never_a_candidate_workspace()
    {
        var root = FakeRoot("noroot");
        var fs = new FakeWorkspaceFileSystem();
        fs.Directories.Add(root);

        var act = () => WorkspacePathResolution.ResolveAndValidate(root, root, fs);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Missing_workspace_under_existing_root_accepted()
    {
        var root = FakeRoot("missing");
        var fs = new FakeWorkspaceFileSystem();
        fs.Directories.Add(root);

        var (workspace, resolvedRoot) = WorkspacePathResolution.ResolveAndValidate(
            Join(root, "new"), root, fs);

        workspace.Should().Be(Join(root, "new"));
        resolvedRoot.Should().Be(root);
    }

    [Fact]
    public void Options_store_canonical_absolute_paths()
    {
        var root = CreateTempRoot();
        try
        {
            var sub = Directory.CreateDirectory(Path.Combine(root, "sub")).FullName;
            var options = new CodexExecOptions(
                "do the work",
                Path.Combine(root, "sub", "..", "sub"),
                root + Path.DirectorySeparatorChar);

            options.WorkspaceDirectory.Should().Be(sub);
            options.ApprovedWorkspaceRoot.Should().Be(new DirectoryInfo(root).FullName.TrimEnd(Path.DirectorySeparatorChar));
            options.WorkspaceDirectory.Should().NotContain("..");
            Path.IsPathFullyQualified(options.WorkspaceDirectory).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Relative_paths_survive_current_directory_change()
    {
        var root = CreateTempRoot();
        var previous = Environment.CurrentDirectory;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "ws"));
            Environment.CurrentDirectory = root;
            var options = new CodexExecOptions("do the work", "ws", ".");

            Environment.CurrentDirectory = Path.GetTempPath();

            options.WorkspaceDirectory.Should().Be(Path.Combine(root, "ws"));
            options.ValidateWorkspaceBeforeLaunch().Should().Be(options.WorkspaceDirectory);
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Options_reject_workspace_equal_to_root()
    {
        var root = CreateTempRoot();
        try
        {
            var act = () => new CodexExecOptions("do the work", root, root);
            act.Should().Throw<ArgumentException>();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Real_escaping_symlink_rejected_or_skipped()
    {
        var root = CreateTempRoot();
        var outside = CreateTempRoot();
        try
        {
            var link = Path.Combine(root, "link");
            try
            {
                Directory.CreateSymbolicLink(link, outside);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                Assert.Skip($"Symlink creation is not permitted here; escape coverage relies on fake-FS units. ({exception.GetType().Name})");
                return;
            }

            var act = () => new CodexExecOptions("do the work", link, root);
            act.Should().Throw<ArgumentException>();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void Validate_before_launch_refuses_swapped_link()
    {
        var root = CreateTempRoot();
        var outside = CreateTempRoot();
        var inside = Directory.CreateDirectory(Path.Combine(root, "real")).FullName;
        try
        {
            var link = Path.Combine(root, "link");
            try
            {
                Directory.CreateSymbolicLink(link, inside);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                Assert.Skip($"Symlink creation is not permitted here; swap coverage relies on fake-FS units. ({exception.GetType().Name})");
                return;
            }

            var options = new CodexExecOptions("do the work", Path.Combine(link, "sub"), root);
            options.ValidateWorkspaceBeforeLaunch().Should().Be(Path.Combine(inside, "sub"));

            Directory.Delete(link);
            Directory.CreateSymbolicLink(link, outside);

            var act = () => options.ValidateWorkspaceBeforeLaunch();
            act.Should().Throw<UnauthorizedAccessException>();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void Case_variant_sibling_follows_platform_identity()
    {
        var parent = CreateTempRoot();
        try
        {
            var root = Directory.CreateDirectory(Path.Combine(parent, "Work")).FullName;
            var sibling = Path.Combine(parent, "WORK", "sub");

            if (OperatingSystem.IsWindows())
            {
                var options = new CodexExecOptions("do the work", sibling, root);
                options.WorkspaceDirectory.Should().EndWith("sub");
            }
            else
            {
                var act = () => new CodexExecOptions("do the work", sibling, root);
                act.Should().Throw<ArgumentException>();
            }
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void Rejects_escaping_intermediate_junction()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Interior-link escape is exercised with a Windows junction; fake-FS units cover the logic.");
            return;
        }

        var parent = CreateTempRoot();
        var outside = CreateTempRoot();
        try
        {
            var root = Directory.CreateDirectory(Path.Combine(parent, "root")).FullName;
            Directory.CreateDirectory(Path.Combine(outside, "sub"));
            var link = Path.Combine(root, "a");
            if (!TryCreateJunction(link, outside))
            {
                Assert.Skip("Junction creation is not permitted here; the escape is verified externally.");
                return;
            }

            // root\a is a junction to outside and root\a\sub exists *through*
            // it. Directory.Exists(root\a\sub) is true while
            // ResolveLinkTarget(root\a\sub) is null, so a leaf-only resolver
            // would accept this escaped path.
            var act = () => new CodexExecOptions("do the work", Path.Combine(link, "sub"), root);
            act.Should().Throw<ArgumentException>();
        }
        finally
        {
            DeleteJunction(Path.Combine(parent, "root", "a"));
            if (Directory.Exists(parent))
            {
                Directory.Delete(parent, recursive: true);
            }

            if (Directory.Exists(outside))
            {
                Directory.Delete(outside, recursive: true);
            }
        }
    }

    [Fact]
    public void Accepts_contained_intermediate_junction()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Interior-link containment is exercised with a Windows junction; fake-FS units cover the logic.");
            return;
        }

        var parent = CreateTempRoot();
        try
        {
            var root = Directory.CreateDirectory(Path.Combine(parent, "root")).FullName;
            var real = Directory.CreateDirectory(Path.Combine(root, "real")).FullName;
            var link = Path.Combine(root, "a");
            if (!TryCreateJunction(link, real))
            {
                Assert.Skip("Junction creation is not permitted here; containment is verified externally.");
                return;
            }

            var options = new CodexExecOptions("do the work", Path.Combine(link, "sub"), root);
            options.WorkspaceDirectory.Should().Be(Path.Combine(real, "sub"));
        }
        finally
        {
            DeleteJunction(Path.Combine(parent, "root", "a"));
            if (Directory.Exists(parent))
            {
                Directory.Delete(parent, recursive: true);
            }
        }
    }

    private static void DeleteJunction(string linkPath)
    {
        // Remove the link itself before any recursive delete: recursing into a
        // junction target fails (and would be destructive).
        try
        {
            if (Directory.Exists(linkPath) || File.Exists(linkPath))
            {
                Directory.Delete(linkPath);
            }
        }
        catch (Exception)
        {
            // Best-effort cleanup.
        }
    }

    private static bool TryCreateJunction(string linkPath, string targetPath)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "cmd.exe",
                $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(10_000);
            return process.ExitCode == 0 && Directory.Exists(linkPath);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string FakeRoot(string name) =>
        Path.Combine(Path.GetTempPath(), "qh02-fake-" + name);

    private static string Join(string first, string second) => Path.Combine(first, second);

    private static string Join(string first, string second, string third) =>
        Path.Combine(first, second, third);

    private static string CreateTempRoot() =>
        Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "qh02-" + Guid.NewGuid().ToString("N"))).FullName;

    private sealed class FakeWorkspaceFileSystem : IWorkspaceFileSystem
    {
        public bool CaseSensitive = true;

        public bool ThrowOnResolve;

        public readonly HashSet<string> Directories = new(StringComparer.Ordinal);

        public readonly HashSet<string> Files = new(StringComparer.Ordinal);

        public readonly Dictionary<string, string?> LinkTargets = new(StringComparer.Ordinal);

        public readonly Dictionary<string, string?> FinalTargets = new(StringComparer.Ordinal);

        public bool DirectoryExists(string path) => Matches(Directories, path);

        public bool FileExists(string path) => Matches(Files, path);

        public string? GetLinkTarget(string path) =>
            LinkTargets.TryGetValue(path, out var target) ? target : null;

        public string? GetFinalTarget(string path)
        {
            if (ThrowOnResolve)
            {
                throw new IOException("Simulated resolution failure.");
            }

            return FinalTargets.TryGetValue(path, out var target) ? target : null;
        }

        public bool IsCaseSensitive(string resolvedRoot) => CaseSensitive;

        private bool Matches(HashSet<string> set, string path)
        {
            // An ancestor of a registered path exists too (the walk probes
            // from the filesystem root down), so treat prefixes as present.
            var comparison = CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var separator = Path.DirectorySeparatorChar;
            foreach (var entry in set)
            {
                if (entry.Equals(path, comparison)
                    || entry.StartsWith(path + separator, comparison))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
