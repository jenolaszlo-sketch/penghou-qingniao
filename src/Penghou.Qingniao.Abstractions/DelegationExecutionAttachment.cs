namespace Penghou.Qingniao;

/// <summary>
/// A single opaque, host-defined execution attachment persisted with one
/// delegation generation. Qingniao stores, copies, and re-presents it across
/// the execution lifecycle, but never interprets <see cref="Value"/>: only the
/// host that produced <see cref="Kind"/> knows how to resolve it (for example
/// the Hufu authority context). At most one attachment is carried per
/// generation; this is deliberately not an extensible metadata collection.
/// </summary>
public sealed record DelegationExecutionAttachment
{
    /// <summary>
    /// Initializes a new instance of the DelegationExecutionAttachment type.
    /// </summary>
    public DelegationExecutionAttachment(string kind, string value)
    {
        Kind = IdentityText.Require(kind, nameof(kind), 256);
        Value = IdentityText.Require(value, nameof(value), 4_096);
    }

    /// <summary>
    /// Gets the host-defined attachment kind. The host that owns the kind
    /// defines its meaning; Qingniao treats it as an opaque label.
    /// </summary>
    public string Kind { get; }

    /// <summary>
    /// Gets the opaque host-defined attachment value. Qingniao never
    /// interprets, validates, or dereferences it.
    /// </summary>
    public string Value { get; }
}
