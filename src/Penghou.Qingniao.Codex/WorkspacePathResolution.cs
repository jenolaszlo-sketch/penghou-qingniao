namespace Penghou.Qingniao.Codex;

/// <summary>Filesystem queries for workspace containment checks, behind a seam for deterministic tests.</summary>
internal interface IWorkspaceFileSystem
{
    /// <summary>Determines whether a directory exists (never throws in the physical implementation).</summary>
    bool DirectoryExists(string path);

    /// <summary>Determines whether a file exists (never throws in the physical implementation).</summary>
    bool FileExists(string path);

    /// <summary>Gets the single-level link target, including dangling links; null when not a link.</summary>
    string? GetLinkTarget(string path);

    /// <summary>Gets the final target of a link chain, or null when not a link.</summary>
    string? GetFinalTarget(string path);

    /// <summary>Determines case sensitivity at the resolved root, conservatively returning true when it cannot be probed.</summary>
    bool IsCaseSensitive(string resolvedRoot);
}

/// <summary>Production filesystem queries for workspace containment checks.</summary>
internal sealed class PhysicalWorkspaceFileSystem : IWorkspaceFileSystem
{
    /// <summary>Gets the shared instance.</summary>
    public static readonly PhysicalWorkspaceFileSystem Instance = new();

    private PhysicalWorkspaceFileSystem()
    {
    }

    /// <inheritdoc />
    public bool DirectoryExists(string path) => Directory.Exists(path);

    /// <inheritdoc />
    public bool FileExists(string path) => File.Exists(path);

    /// <inheritdoc />
    public string? GetLinkTarget(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        // For a dangling symlink the path itself does not exist, yet the
        // link target is still reported, which lets validation resolve (and
        // refuse) escapes before the link is ever executed against.
        return info.LinkTarget;
    }

    /// <inheritdoc />
    public string? GetFinalTarget(string path)
    {
        // ResolveLinkTarget throws for a non-existent path; a path that does
        // not exist cannot be a link (dangling links are handled by
        // GetLinkTarget), so report no target instead of failing the walk.
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            return null;
        }

        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
    }

    /// <inheritdoc />
    public bool IsCaseSensitive(string resolvedRoot)
    {
        // Probe a name we created, not a pre-existing case variant that could
        // be a different directory or symlink on a sensitive filesystem.
        if (!Directory.Exists(resolvedRoot))
        {
            return true;
        }

        var probe = Path.Combine(resolvedRoot, ".qingniao-case-" + Guid.NewGuid().ToString("N") + "a");
        var created = false;
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                created = true;
                return !File.Exists(probe[..^1] + "A");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Exact-case containment is safe when the root cannot be probed.
            return true;
        }
        finally
        {
            if (created)
            {
                try
                {
                    File.Delete(probe);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // A failed probe cleanup must not turn a valid path into an escape.
                }
            }
        }
    }
}

/// <summary>
/// Physical workspace containment: canonical absolute paths, symlink/
/// junction/reparse-point resolution, filesystem-appropriate case rules, and
/// strict candidate containment.
/// </summary>
/// <remarks>
/// Threat model. Path validation is a time-of-check control, not an OS
/// sandbox: it refuses misconfiguration (lexical escapes, links resolving
/// outside the root, case-ambiguous siblings, the root itself) but cannot
/// close a link swapped between validation and spawn. Hosts must therefore
/// (a) re-validate immediately before launch
/// (<see cref="CodexExecOptions.ValidateWorkspaceBeforeLaunch"/>), (b) run
/// each delegation in a dedicated disposable candidate directory the
/// subordinate cannot rewire, and (c) rely on the configured sandbox and OS
/// isolation for the residual window. Any I/O failure during resolution
/// refuses closed rather than guessing. A unique probe distinguishes
/// case-insensitive filesystems from coexisting case-variant directories;
/// when probing is unavailable, exact-case comparison may reject an alias.
/// </remarks>
internal static class WorkspacePathResolution
{
    /// <summary>
    /// Resolves both paths physically and requires the workspace to sit
    /// strictly beneath the approved root.
    /// </summary>
    /// <returns>The canonical workspace and root; both absolute and normalized.</returns>
    /// <exception cref="ArgumentException">Containment cannot be established.</exception>
    public static (string Workspace, string Root) ResolveAndValidate(
        string workspaceDirectory,
        string approvedWorkspaceRoot,
        IWorkspaceFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(workspaceDirectory);
        ArgumentNullException.ThrowIfNull(approvedWorkspaceRoot);
        ArgumentNullException.ThrowIfNull(fileSystem);

        var workspace = ResolvePhysical(
            ToCanonicalAbsolute(workspaceDirectory, nameof(workspaceDirectory)), fileSystem);
        var root = ResolvePhysical(
            ToCanonicalAbsolute(approvedWorkspaceRoot, nameof(approvedWorkspaceRoot)), fileSystem);
        var comparison = DetermineComparison(root, fileSystem);
        if (!IsStrictlyUnder(workspace, root, comparison))
        {
            throw new ArgumentException(
                "The workspace directory must resolve to a dedicated candidate directory strictly beneath the approved workspace root.",
                nameof(workspaceDirectory));
        }

        return (workspace, root);
    }

    internal static string ToCanonicalAbsolute(string value, string parameterName)
    {
        string full;
        try
        {
            full = Path.GetFullPath(value);
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            throw new ArgumentException("The path is not a usable filesystem path.", parameterName, exception);
        }

        if (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar)
        {
            full = full.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        }

        var trimmed = full.TrimEnd([Path.DirectorySeparatorChar]);
        if (trimmed.Length == 0)
        {
            return Path.DirectorySeparatorChar.ToString();
        }

        if (trimmed.Length == 2 && trimmed[1] == ':' && char.IsAsciiLetter(trimmed[0]))
        {
            return trimmed + Path.DirectorySeparatorChar;
        }

        return trimmed;
    }

    internal static string ResolvePhysical(string canonicalAbsolute, IWorkspaceFileSystem fileSystem)
    {
        try
        {
            // Walk from the filesystem root one component at a time so a link
            // anywhere in the path (not just the leaf) is resolved before
            // containment is judged. Resolving only the leaf misses an
            // intermediate junction/symlink that redirects a segment whose
            // child otherwise exists (verified with Windows directory
            // junctions: ResolveLinkTarget on root\a\sub returns null while
            // root\a is a junction).
            var components = SplitComponents(canonicalAbsolute);
            var resolved = components[0];
            var queue = new LinkedList<string>();
            for (var index = 1; index < components.Count; index++)
            {
                queue.AddLast(components[index]);
            }

            // Re-queued link targets can add components; the budget fails
            // closed on pathological link cycles instead of looping.
            var budget = components.Count + 64;
            while (queue.First is { } head)
            {
                if (budget-- <= 0)
                {
                    throw new ArgumentException(
                        "Workspace containment cannot be established: link resolution did not converge.",
                        nameof(canonicalAbsolute));
                }

                var component = head.Value;
                queue.RemoveFirst();
                if (component.Length == 0)
                {
                    continue;
                }

                var candidate = Path.Combine(resolved, component);
                var link = fileSystem.GetLinkTarget(candidate) ?? fileSystem.GetFinalTarget(candidate);
                if (link is not null)
                {
                    var target = ToCanonicalAbsolute(ResolveAgainst(candidate, link), nameof(canonicalAbsolute));
                    var targetComponents = SplitComponents(target);
                    resolved = targetComponents[0];
                    for (var index = targetComponents.Count - 1; index >= 1; index--)
                    {
                        queue.AddFirst(targetComponents[index]);
                    }

                    continue;
                }

                resolved = candidate;
                if (!fileSystem.DirectoryExists(candidate) && !fileSystem.FileExists(candidate))
                {
                    // A child of a non-existent component cannot be a link, so
                    // the remainder is safe to append lexically.
                    while (queue.First is { } rest)
                    {
                        resolved = Path.Combine(resolved, rest.Value);
                        queue.RemoveFirst();
                    }

                    break;
                }
            }

            return ToCanonicalAbsolute(resolved, nameof(canonicalAbsolute));
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException
            or PathTooLongException)
        {
            throw new ArgumentException(
                "Workspace containment cannot be established: the path cannot be resolved.",
                nameof(canonicalAbsolute),
                exception);
        }
    }

    private static List<string> SplitComponents(string canonicalAbsolute)
    {
        var root = Path.GetPathRoot(canonicalAbsolute) ?? Path.DirectorySeparatorChar.ToString();
        var remainder = canonicalAbsolute[root.Length..];
        var segments = remainder.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var components = new List<string>(segments.Length + 1) { root };
        components.AddRange(segments);
        return components;
    }

    internal static StringComparison DetermineComparison(string resolvedRoot, IWorkspaceFileSystem fileSystem)
    {
        return fileSystem.IsCaseSensitive(resolvedRoot)
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
    }

    private static bool IsStrictlyUnder(string candidate, string root, StringComparison comparison)
    {
        // The root itself is never a candidate workspace: execution binds to
        // a dedicated disposable directory, never to a primary checkout.
        if (candidate.Equals(root, comparison))
        {
            return false;
        }

        return candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    private static string ResolveAgainst(string current, string linkTarget) =>
        Path.IsPathFullyQualified(linkTarget)
            ? linkTarget
            : Path.Combine(Path.GetDirectoryName(current) ?? current, linkTarget);
}
