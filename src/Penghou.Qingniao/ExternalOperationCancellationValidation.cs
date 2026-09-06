namespace Penghou.Qingniao;

/// <summary>
/// Applies the same canonical key and prose rules as the public
/// <see cref="ExternalOperationCancelRequest"/> contract.  The coordinator's
/// convenience overload accepts the fields separately, so it must validate
/// them before it creates the canonical request.
/// </summary>
internal static class ExternalOperationCancellationValidation
{
    internal static string RequireKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty identity value is required.", nameof(value));
        }

        if (value.Length > 512)
        {
            throw new ArgumentException("The identity value cannot exceed 512 characters.", nameof(value));
        }

        if (value.Normalize(System.Text.NormalizationForm.FormC) != value
            || value != value.Trim()
            || value.Any(char.IsControl))
        {
            throw new ArgumentException("Identity values must already be in canonical form.", nameof(value));
        }

        return value;
    }

    internal static string RequireReason(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Non-empty prose is required.", nameof(value));
        }

        if (value.Length > 4_096)
        {
            throw new ArgumentException("Prose cannot exceed 4,096 characters.", nameof(value));
        }

        if (value.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
        {
            throw new ArgumentException("Prose cannot contain non-whitespace control characters.", nameof(value));
        }

        return value.Normalize(System.Text.NormalizationForm.FormC)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
    }
}
