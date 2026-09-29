using System.Text;
using FluentAssertions;

namespace Penghou.Qingniao.Codex.Tests;

/// <summary>
/// QH-01 lifecycle regressions: bounded line framing over raw streams plus
/// real local child processes (never paid model calls). Shell one-liners are
/// branched per OS because the suite runs on Windows and Linux.
/// </summary>
public sealed class CodexProcessIOTests
{
    [Fact]
    public async Task Reader_splits_lines_and_strips_cr()
    {
        using var stream = new MemoryStream("a\r\nb\nc"u8.ToArray());

        var lines = await DrainAsync(stream, maxLineBytes: 16);

        lines.Should().Equal("a", "b", "c");
    }

    [Fact]
    public async Task Reader_yields_empty_lines_and_empty_streams()
    {
        using var blank = new MemoryStream("\n"u8.ToArray());
        (await DrainAsync(blank, maxLineBytes: 16)).Should().Equal(string.Empty);

        using var empty = new MemoryStream([]);
        (await DrainAsync(empty, maxLineBytes: 16)).Should().BeEmpty();
    }

    [Fact]
    public async Task Reader_discards_over_cap_lines_but_keeps_draining()
    {
        var payload = "ok1\n" + new string('L', 20_000) + "\n" + "ok2";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        var progress = new BoundedReadProgress();

        var lines = await DrainAsync(stream, maxLineBytes: 16, progress);

        lines.Should().Equal("ok1", "ok2");
        progress.Lines.Should().Be(2);
        progress.DiscardedLines.Should().Be(1);
        progress.DiscardedBytes.Should().Be(20_000);
        progress.Bytes.Should().Be(payload.Length);
    }

    [Fact]
    public async Task Reader_keeps_lines_at_exactly_the_cap()
    {
        using var stream = new MemoryStream("abcd\nabcde\n"u8.ToArray());
        var progress = new BoundedReadProgress();

        var lines = await DrainAsync(stream, maxLineBytes: 4, progress);

        lines.Should().Equal("abcd");
        progress.DiscardedLines.Should().Be(1);
    }

    [Fact]
    public async Task Reader_preserves_multibyte_chars_split_across_chunks()
    {
        var payload = new string('A', 8_190) + "é\ntail";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));

        var lines = await DrainAsync(stream, maxLineBytes: 1_048_576);

        lines.Should().Equal(new string('A', 8_190) + "é", "tail");
    }

    [Fact]
    public async Task Reader_replaces_invalid_utf8_without_throwing()
    {
        using var stream = new MemoryStream([0xFF, 0xFE, (byte)'\n']);

        var lines = await DrainAsync(stream, maxLineBytes: 16);

        lines.Should().Equal("\uFFFD\uFFFD");
    }

    [Fact]
    public async Task Reader_rejects_null_streams_and_nonpositive_caps()
    {
        var actNull = () => DrainAsync(null!, maxLineBytes: 16);
        await actNull.Should().ThrowAsync<ArgumentNullException>();

        using var stream = new MemoryStream("x"u8.ToArray());
        var actCap = () => DrainAsync(stream, maxLineBytes: 0);
        await actCap.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Factory_rejects_nonpositive_line_caps()
    {
        var act = () => new ProcessCodexProcessFactory(maxLineBytes: 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Real_child_flooding_stderr_completes_without_deadlock()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = timeout.Token;
        var factory = new ProcessCodexProcessFactory();
        await using var process = factory.Start(FloodInvocation());

        var stdout = new List<string>();
        var stderr = new List<string>();
        await Task.WhenAll(
            DrainToAsync(process.ReadLinesAsync(ct), stdout, ct),
            DrainToAsync(process.ReadErrorLinesAsync(ct), stderr, ct));
        var exit = await process.WaitForExitAsync(ct);

        exit.Should().Be(0);
        stdout.Should().ContainSingle().Which.Should().Contain("thread.started");
        // Every flooded line is drained: a non-draining reader would block
        // the child on a full stderr pipe and hit the timeout instead.
        stderr.Should().HaveCount(20_000);
        process.Should().BeAssignableTo<ICodexProcessDiagnostics>().Subject
            .Stderr.Bytes.Should().BeGreaterThan(500_000);
    }

    [Fact]
    public async Task Real_child_huge_newline_free_stdout_stays_bounded()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = timeout.Token;
        var factory = new ProcessCodexProcessFactory();
        await using var process = factory.Start(HugeLineInvocation());

        var stdout = new List<string>();
        var stderr = new List<string>();
        await Task.WhenAll(
            DrainToAsync(process.ReadLinesAsync(ct), stdout, ct),
            DrainToAsync(process.ReadErrorLinesAsync(ct), stderr, ct));
        var exit = await process.WaitForExitAsync(ct);

        exit.Should().Be(0);
        stdout.Should().ContainSingle().Which.Should().Contain("thread.started");
        stderr.Should().BeEmpty();
        var diagnostics = (ICodexProcessDiagnostics)process;
        diagnostics.Stdout.DiscardedLines.Should().Be(1);
        diagnostics.Stdout.DiscardedBytes.Should().BeGreaterThan(262_144);
        diagnostics.Stdout.Bytes.Should().BeGreaterThan(262_144);
    }

    [Fact]
    public async Task Kill_exit_and_dispose_races_leave_no_live_child()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = timeout.Token;
        var factory = new ProcessCodexProcessFactory();
        var process = factory.Start(SleeperInvocation());

        var killer = Task.Run(() =>
        {
            for (var index = 0; index < 50; index++)
            {
                process.Kill();
            }
        }, ct);
        await process.WaitForExitAsync(ct);
        await killer;
        for (var index = 0; index < 5; index++)
        {
            process.Kill();
        }

        await process.DisposeAsync();
        await process.DisposeAsync();
    }

    private static async Task<List<string>> DrainAsync(
        Stream stream,
        int maxLineBytes,
        BoundedReadProgress? progress = null)
    {
        var lines = new List<string>();
        await foreach (var line in BoundedLineReader.ReadLinesAsync(
            stream, maxLineBytes, progress, TestContext.Current.CancellationToken))
        {
            lines.Add(line);
        }

        return lines;
    }

    private static async Task DrainToAsync(
        IAsyncEnumerable<string> lines,
        List<string> sink,
        CancellationToken cancellationToken)
    {
        await foreach (var line in lines.WithCancellation(cancellationToken))
        {
            sink.Add(line);
        }
    }

    private static CodexProcessInvocation FloodInvocation()
    {
        const string thread = """{"type":"thread.started","thread_id":"thread-flood"}""";
        if (OperatingSystem.IsWindows())
        {
            return new CodexProcessInvocation(
                "cmd.exe",
                ["/c", $"echo {thread} & for /L %i in (1,1,20000) do @echo FLOOD-0123456789abcdef0123456789abcdef 1>&2"],
                Path.GetTempPath());
        }

        return new CodexProcessInvocation(
            "/bin/sh",
            ["-c", $"echo '{thread}'; i=0; while [ $i -lt 20000 ]; do echo FLOOD-0123456789abcdef0123456789abcdef >&2; i=$((i + 1)); done"],
            Path.GetTempPath());
    }

    private static CodexProcessInvocation HugeLineInvocation()
    {
        const string thread = """{"type":"thread.started","thread_id":"thread-huge"}""";
        if (OperatingSystem.IsWindows())
        {
            var filler = new string('H', 3_000);
            return new CodexProcessInvocation(
                "cmd.exe",
                ["/c", $"echo {thread} & (for /L %i in (1,1,300) do @<nul set /p \"={filler}\") & echo."],
                Path.GetTempPath());
        }

        return new CodexProcessInvocation(
            "/bin/sh",
            ["-c", $"echo '{thread}'; head -c 900000 /dev/zero | tr '\\000' 'H'; echo"],
            Path.GetTempPath());
    }

    private static CodexProcessInvocation SleeperInvocation()
    {
        if (OperatingSystem.IsWindows())
        {
            return new CodexProcessInvocation(
                "cmd.exe",
                ["/c", "ping -n 30 127.0.0.1 >nul"],
                Path.GetTempPath());
        }

        return new CodexProcessInvocation(
            "/bin/sh",
            ["-c", "sleep 30"],
            Path.GetTempPath());
    }
}
