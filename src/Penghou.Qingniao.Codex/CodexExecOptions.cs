namespace Penghou.Qingniao.Codex;

/// <summary>
/// One Codex execution: the task prompt, the approved workspace it may touch,
/// and the bounds the host enforces. The workspace directory must sit under
/// <see cref="ApprovedWorkspaceRoot"/>; the adapter refuses anything else.
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
        WorkspaceDirectory = RequirePath(workspaceDirectory, nameof(workspaceDirectory));
        ApprovedWorkspaceRoot = RequirePath(approvedWorkspaceRoot, nameof(approvedWorkspaceRoot));
        if (!IsUnderRoot(WorkspaceDirectory, ApprovedWorkspaceRoot))
        {
            throw new ArgumentException(
                "The workspace directory must sit under the approved workspace root.",
                nameof(workspaceDirectory));
        }

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
    /// <summary>Gets the workspace Codex runs in.</summary>
    public string WorkspaceDirectory { get; }
    /// <summary>Gets the approved root the workspace must sit under.</summary>
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

    private static bool IsUnderRoot(string directory, string root)
    {
        var fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        return fullDirectory.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
            || fullDirectory.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
