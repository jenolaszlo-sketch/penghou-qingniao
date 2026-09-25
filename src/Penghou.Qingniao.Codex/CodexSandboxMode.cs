namespace Penghou.Qingniao.Codex;

/// <summary>
/// Sandbox policy for model-generated shell commands. Only the two
/// host-approved modes are exposed; the CLI's unsandboxed bypass flags are
/// never emitted by this adapter.
/// </summary>
public enum CodexSandboxMode
{
    /// <summary>Model-generated commands may only read.</summary>
    ReadOnly = 0,
    /// <summary>Model-generated commands may write inside the workspace.</summary>
    WorkspaceWrite = 1,
}
