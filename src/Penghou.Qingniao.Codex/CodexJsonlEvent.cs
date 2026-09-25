using System.Text.Json;

namespace Penghou.Qingniao.Codex;

/// <summary>
/// Observed `codex exec --json` event shapes. Only shapes seen on the wire are
/// modeled; anything else arrives as <see cref="Unknown"/> and is counted, so
/// a CLI schema change can never silently become a false fact.
/// </summary>
public abstract record CodexJsonlEvent
{
    private CodexJsonlEvent()
    {
    }

    /// <summary>First event of a run; carries the durable thread identity.</summary>
    public sealed record ThreadStarted(string ThreadId) : CodexJsonlEvent;

    /// <summary>A model turn began.</summary>
    public sealed record TurnStarted : CodexJsonlEvent
    {
        /// <summary>Gets the singleton turn-started event.</summary>
        public static TurnStarted Instance { get; } = new();

        private TurnStarted()
        {
        }
    }

    /// <summary>A transport-level error event.</summary>
    public sealed record ErrorEvent(string Message) : CodexJsonlEvent;

    /// <summary>A turn ended in failure.</summary>
    public sealed record TurnFailed(string Message) : CodexJsonlEvent;

    /// <summary>An event shape this adapter does not model.</summary>
    public sealed record Unknown(string Type, string Raw) : CodexJsonlEvent;

    /// <summary>
    /// Parses one JSONL line. Blank lines return null; malformed JSON returns
    /// an <see cref="Unknown"/> with an empty type.
    /// </summary>
    public static CodexJsonlEvent? TryParse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return new Unknown(string.Empty, Truncate(line));
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var kind))
            {
                return new Unknown(string.Empty, Truncate(line));
            }

            var type = kind.GetString() ?? string.Empty;
            return type switch
            {
                "thread.started" when root.TryGetProperty("thread_id", out var id)
                    => new ThreadStarted(id.GetString() ?? string.Empty),
                "turn.started" => TurnStarted.Instance,
                "error" when root.TryGetProperty("message", out var message)
                    => new ErrorEvent(message.GetString() ?? string.Empty),
                "turn.failed" when root.TryGetProperty("error", out var error)
                    && error.TryGetProperty("message", out var nested)
                    => new TurnFailed(nested.GetString() ?? string.Empty),
                _ => new Unknown(type, Truncate(line)),
            };
        }
    }

    private static string Truncate(string value) =>
        value.Length <= 1_024 ? value : value[..1_024];
}
