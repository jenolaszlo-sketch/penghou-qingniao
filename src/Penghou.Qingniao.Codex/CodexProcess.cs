using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Penghou.Qingniao.Codex;

/// <summary>One Codex CLI invocation: executable, arguments, and working directory.</summary>
public sealed record CodexProcessInvocation(
    string CliPath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory);

/// <summary>A running Codex process behind a test seam.</summary>
/// <remarks>
/// Both output streams must be drained: a child blocks once either pipe
/// fills, so reading stdout alone can deadlock a stderr-noisy child. Line
/// length is capped before allocation (see
/// <see cref="ProcessCodexProcessFactory"/>); over-long lines are discarded
/// while draining continues, so the child can always finish.
/// </remarks>
public interface ICodexProcess : IAsyncDisposable
{
    /// <summary>Reads stdout lines until the process ends. Lines longer than the factory cap are discarded, never truncated into events.</summary>
    IAsyncEnumerable<string> ReadLinesAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads stderr lines until the process ends. Drain concurrently with <see cref="ReadLinesAsync"/>; content is diagnostics, never execution events.</summary>
    IAsyncEnumerable<string> ReadErrorLinesAsync(CancellationToken cancellationToken = default);

    /// <summary>Waits for process exit.</summary>
    Task<int> WaitForExitAsync(CancellationToken cancellationToken = default);

    /// <summary>Kills a running process. Safe to call after exit or more than once.</summary>
    void Kill();
}

/// <summary>Spawns Codex processes without a shell.</summary>
public interface ICodexProcessFactory
{
    /// <summary>Starts one Codex invocation.</summary>
    ICodexProcess Start(CodexProcessInvocation invocation);
}

/// <summary>Byte counts for one drained stream: yielded lines and discarded over-long content.</summary>
internal sealed class BoundedReadProgress
{
    /// <summary>Gets the number of lines yielded to the reader.</summary>
    public long Lines { get; private set; }

    /// <summary>Gets the raw content bytes consumed from the stream, including discarded lines.</summary>
    public long Bytes { get; private set; }

    /// <summary>Gets the number of lines discarded for exceeding the byte cap.</summary>
    public long DiscardedLines { get; private set; }

    /// <summary>Gets the content bytes discarded for exceeding the byte cap.</summary>
    public long DiscardedBytes { get; private set; }

    internal void AddBytes(long count) => Bytes += count;

    internal void AddLine() => Lines++;

    internal void AddDiscarded(long contentBytes)
    {
        DiscardedLines++;
        DiscardedBytes += contentBytes;
    }

    internal void AddDiscardedBytes(long count) => DiscardedBytes += count;
}

/// <summary>
/// Newline-delimited line reader over a raw byte stream. Lines are framed at
/// the byte level (UTF-8 never embeds a bare 0x0A inside a multi-byte
/// sequence) and a per-line byte cap is enforced before any string is
/// allocated: over-long lines are discarded while draining continues, so a
/// malicious or verbose child can neither exhaust memory nor block on a full
/// pipe. A trailing carriage return is stripped; the final line need not end
/// with a newline.
/// </summary>
internal static class BoundedLineReader
{
    public static async IAsyncEnumerable<string> ReadLinesAsync(
        Stream stream,
        int maxLineBytes,
        BoundedReadProgress? progress = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxLineBytes, 0);

        const int chunkBytes = 8_192;
        var chunk = new byte[chunkBytes];
        var pending = new byte[Math.Min(chunkBytes, maxLineBytes)];
        var pendingCount = 0;
        var discarding = false;

        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            progress?.AddBytes(read);
            var offset = 0;
            while (offset < read)
            {
                var newline = Array.IndexOf(chunk, (byte)'\n', offset, read - offset);
                var end = newline < 0 ? read : newline;
                if (discarding)
                {
                    progress?.AddDiscardedBytes(end - offset);
                }
                else
                {
                    var prior = pendingCount;
                    var room = maxLineBytes - pendingCount;
                    var take = Math.Max(0, Math.Min(end - offset, room));
                    if (take > 0)
                    {
                        if (pendingCount + take > pending.Length)
                        {
                            Array.Resize(ref pending, pendingCount + take);
                        }

                        Buffer.BlockCopy(chunk, offset, pending, pendingCount, take);
                        pendingCount += take;
                    }

                    if (end - offset > take)
                    {
                        // The whole line content is dropped: previously kept
                        // bytes plus the current segment. Draining continues.
                        discarding = true;
                        progress?.AddDiscarded(prior + (end - offset));
                        pendingCount = 0;
                    }
                }

                if (newline < 0)
                {
                    break;
                }

                if (!discarding)
                {
                    progress?.AddLine();
                    yield return DecodeLine(pending, pendingCount);
                }

                discarding = false;
                pendingCount = 0;
                offset = newline + 1;
            }
        }

        if (pendingCount > 0 && !discarding)
        {
            progress?.AddLine();
            yield return DecodeLine(pending, pendingCount);
        }
    }

    private static string DecodeLine(byte[] pending, int count)
    {
        var end = count;
        if (end > 0 && pending[end - 1] == (byte)'\r')
        {
            end--;
        }

        return Encoding.UTF8.GetString(pending, 0, end);
    }
}

/// <summary>Observability for a production child: per-stream drain counters.</summary>
internal interface ICodexProcessDiagnostics
{
    /// <summary>Gets the stdout drain counters.</summary>
    BoundedReadProgress Stdout { get; }

    /// <summary>Gets the stderr drain counters.</summary>
    BoundedReadProgress Stderr { get; }
}

/// <summary>Production factory: argv-array spawn, no shell, redirected stdout and stderr.</summary>
public sealed class ProcessCodexProcessFactory : ICodexProcessFactory
{
    private readonly int maxLineBytes;

    /// <summary>Creates a factory with a per-line output cap.</summary>
    /// <param name="maxLineBytes">
    /// Maximum content bytes retained for one stdout or stderr line before the
    /// line is discarded. The cap is enforced while framing raw bytes, so a
    /// newline-free flood can never force an arbitrarily large allocation.
    /// Draining continues past discarded lines so the child always finishes.
    /// Total retained output stays bounded separately by the adapter transcript
    /// budget (<c>CodexExecOptions.MaxOutputBytes</c>).
    /// </param>
    public ProcessCodexProcessFactory(int maxLineBytes = 262_144)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxLineBytes, 0);
        this.maxLineBytes = maxLineBytes;
    }

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
        // `codex exec` waits for stdin EOF when input is redirected; the
        // adapter never drives interactive input, so close it at spawn or
        // every invocation hangs before the first event.
        process.StandardInput.Close();
        return new ProcessCodexProcess(process, maxLineBytes);
    }

    private sealed class ProcessCodexProcess(Process process, int maxLineBytes)
        : ICodexProcess, ICodexProcessDiagnostics
    {
        private readonly BoundedReadProgress stdout = new();
        private readonly BoundedReadProgress stderr = new();

        public BoundedReadProgress Stdout => stdout;

        public BoundedReadProgress Stderr => stderr;

        public IAsyncEnumerable<string> ReadLinesAsync(CancellationToken cancellationToken = default) =>
            BoundedLineReader.ReadLinesAsync(process.StandardOutput.BaseStream, maxLineBytes, stdout, cancellationToken);

        public IAsyncEnumerable<string> ReadErrorLinesAsync(CancellationToken cancellationToken = default) =>
            BoundedLineReader.ReadLinesAsync(process.StandardError.BaseStream, maxLineBytes, stderr, cancellationToken);

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
            // Backstop: the pump owns exit/drain/disposal coordination, but a
            // process handed out here must never stay alive past disposal.
            Kill();
            process.Dispose();
            await ValueTask.CompletedTask;
        }
    }
}
