namespace Penghou.Qingniao.Codex;

/// <summary>
/// One Codex execution: the task prompt, the approved workspace it may touch,
/// and the bounds the host enforces. Both directories are stored as canonical
/// absolute paths with symlinks, junctions and reparse points resolved: the
/// workspace must sit strictly beneath <see cref="ApprovedWorkspaceRoot"/> on
/// the physical filesystem, never be the root itself, and never escape it
/// through a link. Re-validate with <see cref="ValidateWorkspaceBeforeLaunch"/>
/// immediately before spawning; path checks alone are not a sandbox.
/// </summary>
public sealed record CodexExecOptions
{
    /// <summary>Creates Codex execution options with validation.</summary>
    public CodexExecOptions(
        string prompt,
        string workspaceDirectory,
        string approvedWorkspaceRoot,
        string cliPath = "codex",
        CodexSandboxMode sandbox = CodexSandboxMode.WorkspaceWrite,
        string? model = null,
        string? outputSchemaPath = null,
        long maxOutputBytes = 1_048_576,
        bool ephemeral = false)
    {
        Prompt = RequireText(prompt, nameof(prompt), 32_768);
        var workspace = RequirePath(workspaceDirectory, nameof(workspaceDirectory));
        var root = RequirePath(approvedWorkspaceRoot, nameof(approvedWorkspaceRoot));
        (WorkspaceDirectory, ApprovedWorkspaceRoot) = WorkspacePathResolution.ResolveAndValidate(
            workspace, root, PhysicalWorkspaceFileSystem.Instance);

        CliPath = RequireText(cliPath, nameof(cliPath), 1_024);
        if (!Enum.IsDefined(sandbox))
        {
            throw new ArgumentOutOfRangeException(nameof(sandbox));
        }

        Model = model is null ? null : RequireText(model, nameof(model), 256);
        OutputSchemaPath = outputSchemaPath is null
            ? null
            : RequireText(outputSchemaPath, nameof(outputSchemaPath), 1_024);
        if (maxOutputBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxOutputBytes));
        }

        Sandbox = sandbox;
        MaxOutputBytes = maxOutputBytes;
        Ephemeral = ephemeral;
    }

    /// <summary>Gets the task instruction sent to Codex.</summary>
    public string Prompt { get; }
    /// <summary>Gets the workspace Codex runs in: canonical absolute, physically resolved, strictly beneath the approved root.</summary>
    public string WorkspaceDirectory { get; }
    /// <summary>Gets the approved root the workspace must sit under: canonical absolute and physically resolved.</summary>
    public string ApprovedWorkspaceRoot { get; }
    /// <summary>Gets the CLI executable name or path.</summary>
    public string CliPath { get; }
    /// <summary>Gets the sandbox policy.</summary>
    public CodexSandboxMode Sandbox { get; }
    /// <summary>Gets the optional model override.</summary>
    public string? Model { get; }
    /// <summary>Gets the optional JSON Schema path for the final response.</summary>
    public string? OutputSchemaPath { get; }
    /// <summary>Gets the maximum captured stdout bytes.</summary>
    public long MaxOutputBytes { get; }
    /// <summary>Gets whether session files are persisted (false keeps resume).</summary>
    public bool Ephemeral { get; }

    private static string RequireText(string? value, string name, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A value is required.", name);
        }

        var trimmed = value.Trim();
        if (trimmed.Length > maximumLength || trimmed.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("A value is too long or contains null characters.", name);
        }

        return trimmed;
    }

    private static string RequirePath(string? value, string name) => RequireText(value, name, 32_768);

    /// <summary>
    /// Re-validates physical containment immediately before launch and returns
    /// the canonical workspace directory to spawn in.
    /// </summary>
    /// <remarks>
    /// Construction-time validation cannot see links swapped afterwards, so
    /// the adapter (and any direct host use) must call this right before
    /// spawning and refuse on failure. A changing filesystem is an access
    /// refusal, not a caller argument error.
    /// </remarks>
    /// <returns>The canonical workspace directory.</returns>
    /// <exception cref="UnauthorizedAccessException">Containment can no longer be established.</exception>
    public string ValidateWorkspaceBeforeLaunch()
    {
        try
        {
            var (workspace, _) = WorkspacePathResolution.ResolveAndValidate(
                WorkspaceDirectory, ApprovedWorkspaceRoot, PhysicalWorkspaceFileSystem.Instance);
            return workspace;
        }
        catch (ArgumentException exception)
        {
            throw new UnauthorizedAccessException(
                "The workspace is no longer contained beneath its approved root; launch is refused.",
                exception);
        }
    }
}
