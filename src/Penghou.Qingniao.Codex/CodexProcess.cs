using System.Diagnostics;

namespace Penghou.Qingniao.Codex;

/// <summary>One Codex CLI invocation: executable, arguments, and working directory.</summary>
public sealed record CodexProcessInvocation(
    string CliPath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory);

/// <summary>A running Codex process behind a test seam.</summary>
public interface ICodexProcess : IAsyncDisposable
{
    /// <summary>Reads stdout lines until the process ends or the byte budget is hit.</summary>
    IAsyncEnumerable<string> ReadLinesAsync(CancellationToken cancellationToken = default);

    /// <summary>Waits for process exit.</summary>
    Task<int> WaitForExitAsync(CancellationToken cancellationToken = default);

    /// <summary>Kills a running process.</summary>
    void Kill();
}

/// <summary>Spawns Codex processes without a shell.</summary>
public interface ICodexProcessFactory
{
    /// <summary>Starts one Codex invocation.</summary>
    ICodexProcess Start(CodexProcessInvocation invocation);
}

/// <summary>Production factory: argv-array spawn, no shell, redirected stdout.</summary>
public sealed class ProcessCodexProcessFactory : ICodexProcessFactory
{
    /// <summary>Starts one Codex invocation.</summary>
    public ICodexProcess Start(CodexProcessInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var start = new ProcessStartInfo(invocation.CliPath)
        {
            WorkingDirectory = invocation.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        foreach (var argument in invocation.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        var process = Process.Start(start)
            ?? throw new InvalidOperationException($"Could not start '{invocation.CliPath}'.");
        return new ProcessCodexProcess(process);
    }

    private sealed class ProcessCodexProcess(Process process) : ICodexProcess
    {
        public async IAsyncEnumerable<string> ReadLinesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
            {
                yield return line;
            }
        }

        public async Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode;
        }

        public void Kill()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Already exited between the check and the kill.
            }
        }

        public async ValueTask DisposeAsync()
        {
            process.Dispose();
            await ValueTask.CompletedTask;
        }
    }
}
