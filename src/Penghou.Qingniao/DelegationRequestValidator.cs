namespace Penghou.Qingniao;

/// <summary>Validates the bounded, canonical request contract accepted by Qingniao.</summary>
public static class DelegationRequestValidator
{
    private const int MaximumListItems = 256;
    private const int MaximumTotalTextLength = 1_048_576;

    /// <summary>Validates request identity, content, and budgets.</summary>
    /// <exception cref="ArgumentException">Thrown when required text is missing or non-canonical.</exception>
    public static void Validate(DelegationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        RequireCanonicalIdentityText(request.RequestKey, nameof(request.RequestKey), 256);
        RequireText(request.Objective, nameof(request.Objective), 16_384);
        RequireCanonicalIdentityText(request.Provider, nameof(request.Provider), 512);

        ArgumentNullException.ThrowIfNull(request.Workspace);
        RequireCanonicalIdentityText(request.Workspace.Provider, nameof(request.Workspace.Provider), 128);
        RequireCanonicalIdentityText(request.Workspace.Identifier, nameof(request.Workspace.Identifier), 2_048);
        if (request.Workspace.Revision is not null)
        {
            RequireCanonicalIdentityText(request.Workspace.Revision, nameof(request.Workspace.Revision), 2_048);
        }

        request.AdmissionFence?.Validate();

        if (request.ParentGrantId is not null)
        {
            RequireCanonicalIdentityText(request.ParentGrantId, nameof(request.ParentGrantId), 256);
        }

        if (request.RequestedAuthority is { } authority)
        {
            ValidateRequestedAuthority(authority);
        }

        var totalTextLength = (long)request.RequestKey.Length + request.Objective.Length
            + request.Workspace.Provider.Length + request.Workspace.Identifier.Length
            + (request.Workspace.Revision?.Length ?? 0);
        totalTextLength += ValidateTextList(request.AcceptanceCriteria, nameof(request.AcceptanceCriteria), required: true);
        totalTextLength += ValidateTextList(request.Constraints, nameof(request.Constraints), required: false);
        if (totalTextLength > MaximumTotalTextLength)
        {
            throw new ArgumentException(
                $"The total request text cannot exceed {MaximumTotalTextLength} characters.",
                nameof(request));
        }

        ArgumentNullException.ThrowIfNull(request.Budget);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Budget.MaximumWorkerCalls, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Budget.MaximumRetries, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Budget.MaximumParallelWorkers, 1);

        if (request.Budget.MaximumDuration is { } duration && duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.Budget.MaximumDuration),
                duration,
                "Maximum duration must be positive when supplied.");
        }
    }

    /// <summary>
    /// Validates requested-authority transport shape only: bounded labels,
    /// well-formed scopes, and a non-empty validity interval. Canonical
    /// authority semantics (normalization, containment, delegability) belong
    /// to Hufu at derivation, never here.
    /// </summary>
    private static void ValidateRequestedAuthority(RequestedAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (authority.Actions.Count is < 1 or > 64)
            throw new ArgumentException("One or more bounded action names are required.", nameof(authority));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in authority.Actions)
        {
            RequireCanonicalIdentityText(action, nameof(authority), 128);
            if (!seen.Add(action))
                throw new ArgumentException("Action names must be distinct.", nameof(authority));
        }

        RequireCanonicalIdentityText(authority.Scope.WorkspaceId, nameof(authority), 256);
        RequireAuthorityPath(authority.Scope.RelativePath, nameof(authority));
        if (authority.Exclusions.Count > 128)
            throw new ArgumentException("Too many exclusions.", nameof(authority));
        foreach (var exclusion in authority.Exclusions)
        {
            ArgumentNullException.ThrowIfNull(exclusion, nameof(authority));
            RequireCanonicalIdentityText(exclusion.WorkspaceId, nameof(authority), 256);
            RequireAuthorityPath(exclusion.RelativePath, nameof(authority));
            if (!Enum.IsDefined(exclusion.Kind))
                throw new ArgumentOutOfRangeException(nameof(authority));
        }

        if (!Enum.IsDefined(authority.Scope.Kind))
            throw new ArgumentOutOfRangeException(nameof(authority));
        if (authority.NotBefore >= authority.ExpiresAt)
            throw new ArgumentException("Requested validity must be a non-empty interval.", nameof(authority));
    }

    private static void RequireAuthorityPath(string? value, string parameterName)
    {
        if (value is null)
            throw new ArgumentNullException(parameterName);
        if (value.Length > 512 || value.Any(char.IsControl))
            throw new ArgumentException("Authority paths are bounded text without control characters.", parameterName);
    }

    private static int ValidateTextList(
        IReadOnlyList<string>? values,
        string parameterName,
        bool required)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);

        if (required && values.Count == 0)
        {
            throw new ArgumentException("At least one value is required.", parameterName);
        }

        if (values.Count > MaximumListItems)
        {
            throw new ArgumentException($"A list cannot contain more than {MaximumListItems} values.", parameterName);
        }

        var totalLength = 0;
        for (var index = 0; index < values.Count; index++)
        {
            RequireText(values[index], $"{parameterName}[{index}]", 4_096);
            totalLength += values[index].Length;
        }

        return totalLength;
    }

    private static void RequireText(string? value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty value is required.", parameterName);
        }

        if (value.Length > maximumLength)
        {
            throw new ArgumentException(
                $"The value cannot exceed {maximumLength} characters.",
                parameterName);
        }
    }

    private static void RequireCanonicalIdentityText(string? value, string parameterName, int maximumLength)
    {
        RequireText(value, parameterName, maximumLength);
        if (value!.Normalize(System.Text.NormalizationForm.FormC) != value
            || value != value.Trim()
            || value.Contains('\r')
            || value.Contains('\n'))
        {
            throw new ArgumentException("Identity text must already be in canonical form.", parameterName);
        }
    }
}
