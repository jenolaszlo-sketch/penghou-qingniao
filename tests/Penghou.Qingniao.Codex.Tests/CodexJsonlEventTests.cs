using FluentAssertions;

namespace Penghou.Qingniao.Codex.Tests;

/// <summary>
/// Parser unit tests over observed `codex exec --json` shapes, including the
/// verbatim recorded probe output. Turn-completion and usage shapes follow the
/// 2026-09-26 live observations (see docs/codex-protocol-support.md); the
/// full CLI schema remains unverified, so parsing stays tolerant.
/// </summary>
public sealed class CodexJsonlEventTests
{
    [Fact]
    public void Blank_lines_parse_as_null()
    {
        CodexJsonlEvent.TryParse(null).Should().BeNull();
        CodexJsonlEvent.TryParse("").Should().BeNull();
        CodexJsonlEvent.TryParse("   ").Should().BeNull();
    }

    [Fact]
    public void Malformed_json_parses_as_unknown_with_empty_type()
    {
        var parsed = CodexJsonlEvent.TryParse("{not json");
        var unknown = Assert.IsType<CodexJsonlEvent.Unknown>(parsed);
        unknown.Type.Should().BeEmpty();
        unknown.Raw.Should().Be("{not json");
    }

    [Fact]
    public void Recorded_thread_started_carries_the_durable_identity()
    {
        var parsed = CodexJsonlEvent.TryParse(
            """{"type":"thread.started","thread_id":"01a0d765-251e-71c2-9ed5-fb006e9c7623"}""");
        var started = Assert.IsType<CodexJsonlEvent.ThreadStarted>(parsed);
        started.ThreadId.Should().Be("01a0d765-251e-71c2-9ed5-fb006e9c7623");
    }

    [Fact]
    public void Recorded_turn_shapes_parse()
    {
        CodexJsonlEvent.TryParse("""{"type":"turn.started"}""")
            .Should().BeSameAs(CodexJsonlEvent.TurnStarted.Instance);
        var failed = Assert.IsType<CodexJsonlEvent.TurnFailed>(CodexJsonlEvent.TryParse(
            """{"type":"turn.failed","error":{"message":"boom"}}"""));
        failed.Message.Should().Be("boom");
    }

    [Fact]
    public void Recorded_error_shape_parses()
    {
        var parsed = Assert.IsType<CodexJsonlEvent.ErrorEvent>(CodexJsonlEvent.TryParse(
            """{"type":"error","message":"You hit your usage limit."}"""));
        parsed.Message.Should().Be("You hit your usage limit.");
    }

    [Fact]
    public void Unmodeled_shapes_are_preserved_as_unknown()
    {
        var parsed = Assert.IsType<CodexJsonlEvent.Unknown>(CodexJsonlEvent.TryParse(
            """{"type":"item.completed","item":{"id":"x"}}"""));
        parsed.Type.Should().Be("item.completed");
        parsed.Raw.Should().Contain("item.completed");
    }

    [Fact]
    public void Missing_type_parses_as_unknown()
    {
        var parsed = Assert.IsType<CodexJsonlEvent.Unknown>(CodexJsonlEvent.TryParse(
            """{"hello":"world"}"""));
        parsed.Type.Should().BeEmpty();
    }

    [Fact]
    public void Turn_completed_with_nested_usage_parses()
    {
        var parsed = Assert.IsType<CodexJsonlEvent.TurnCompleted>(CodexJsonlEvent.TryParse(
            """{"type":"turn.completed","usage":{"input_tokens":120,"cached_input_tokens":30,"output_tokens":45}}"""));
        parsed.Usage.Should().NotBeNull();
        parsed.Usage!.InputTokens.Should().Be(120);
        parsed.Usage.CachedInputTokens.Should().Be(30);
        parsed.Usage.OutputTokens.Should().Be(45);
    }

    [Fact]
    public void Turn_completed_accepts_flat_usage_and_partial_shapes()
    {
        var flat = Assert.IsType<CodexJsonlEvent.TurnCompleted>(CodexJsonlEvent.TryParse(
            """{"type":"turn.completed","input_tokens":7,"output_tokens":3}"""));
        flat.Usage.Should().Be(new CodexJsonlEvent.CodexUsage(7, null, 3));

        var bare = Assert.IsType<CodexJsonlEvent.TurnCompleted>(CodexJsonlEvent.TryParse(
            """{"type":"turn.completed"}"""));
        bare.Usage.Should().BeNull();
    }

    [Fact]
    public void Turn_completed_keeps_malformed_usage_unknown()
    {
        var text = Assert.IsType<CodexJsonlEvent.TurnCompleted>(CodexJsonlEvent.TryParse(
            """{"type":"turn.completed","usage":{"input_tokens":"many"}}"""));
        text.Usage.Should().BeNull();

        var negative = Assert.IsType<CodexJsonlEvent.TurnCompleted>(CodexJsonlEvent.TryParse(
            """{"type":"turn.completed","usage":{"input_tokens":-1}}"""));
        negative.Usage.Should().BeNull();
    }
}
