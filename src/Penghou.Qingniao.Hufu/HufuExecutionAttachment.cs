using System.Text;
using Penghou.Hufu;

namespace Penghou.Qingniao.Hufu;

/// <summary>
/// Encodes a Hufu child execution context as the opaque value of a
/// <see cref="DelegationExecutionAttachment"/>. The attachment carries only
/// what the execution side needs to reconstruct the child
/// <see cref="AuthenticatedAuthorityContext"/>: no lineage, parent grant,
/// approval tuple, or delegability data. Qingniao never interprets the value;
/// the execution side must resolve it through <see cref="TryResolve"/> and
/// refuse to run when resolution fails (wrong kind, malformed value, or
/// unresolvable context), never falling back to running without authority.
/// </summary>
public static class HufuExecutionAttachment
{
    /// <summary>Well-known attachment kind produced by the Hufu preflight.</summary>
    public const string Kind = "penghou.hufu.authority-context";

    private const string Prefix = "hufu-context-v1";

    /// <summary>Encodes a child execution context as an opaque attachment value.</summary>
    public static string Encode(AuthenticatedAuthorityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return string.Join(
            ".",
            Prefix,
            Field(context.TenantId),
            Field(context.SubjectId),
            Field(context.RunId),
            Field(context.RevisionId),
            Field(context.FenceId));
    }

    /// <summary>
    /// Decodes an attachment value to its child execution context. Throws on
    /// any malformed input; prefer <see cref="TryResolve"/> at execution
    /// boundaries.
    /// </summary>
    public static AuthenticatedAuthorityContext Decode(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new ArgumentException("An execution attachment value is required.", nameof(value));
        }

        var parts = value.Split('.');
        if (parts.Length != 6 || !string.Equals(parts[0], Prefix, StringComparison.Ordinal))
        {
            throw new FormatException("The execution attachment value is not a Hufu authority context.");
        }

        return new AuthenticatedAuthorityContext(
            Unfield(parts[1], nameof(value)),
            Unfield(parts[2], nameof(value)),
            Unfield(parts[3], nameof(value)),
            Unfield(parts[4], nameof(value)),
            Unfield(parts[5], nameof(value)));
    }

    /// <summary>
    /// Resolves an execution attachment to its Hufu child execution context.
    /// Returns false (never throws) for a missing attachment, a foreign kind,
    /// or a malformed value. A false result must fail closed: do not construct
    /// an authorized execution from it.
    /// </summary>
    public static bool TryResolve(
        DelegationExecutionAttachment? attachment,
        out AuthenticatedAuthorityContext? context)
    {
        context = null;
        if (attachment is null
            || !string.Equals(attachment.Kind, Kind, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var resolved = Decode(attachment.Value);
            if (!AuthorityValidation.ValidContext(resolved))
            {
                return false;
            }

            context = resolved;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return false;
        }
    }

    private static string Field(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new ArgumentException("An authority context field is required.", nameof(value));
        }

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    }

    private static string Unfield(string value, string parameterName)
    {
        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
        catch (FormatException exception)
        {
            throw new FormatException("The execution attachment value is not a Hufu authority context.", exception);
        }

        if (string.IsNullOrEmpty(decoded))
        {
            throw new FormatException("The execution attachment value is not a Hufu authority context.");
        }

        return decoded;
    }
}
