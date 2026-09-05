namespace Penghou.Qingniao;

/// <summary>Validates the bounded, canonical request contract accepted by Qingniao.</summary>
public static class DelegationRequestValidator
{
    private const int MaximumListItems = 256;
    private const int MaximumTotalTextLength = 1_048_576;

    /// <summary>Validates request identity, content, budgets, and supported strategy.</summary>
    /// <exception cref="ArgumentException">Thrown when required text is missing or non-canonical.</exception>
    /// <exception cref="NotSupportedException">Thrown when the strategy is not implemented.</exception>
    public static void Validate(DelegationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        RequireCanonicalIdentityText(request.RequestKey, nameof(request.RequestKey), 256);
        RequireText(request.Objective, nameof(request.Objective), 16_384);

        ArgumentNullException.ThrowIfNull(request.Workspace);
        RequireCanonicalIdentityText(request.Workspace.Provider, nameof(request.Workspace.Provider), 128);
        RequireCanonicalIdentityText(request.Workspace.Identifier, nameof(request.Workspace.Identifier), 2_048);
        if (request.Workspace.Revision is not null)
        {
            RequireCanonicalIdentityText(request.Workspace.Revision, nameof(request.Workspace.Revision), 2_048);
        }

        request.PlanRevision?.Validate();

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

        if (request.Strategy != DelegationStrategy.Implement)
        {
            throw new NotSupportedException(
                $"Delegation strategy '{request.Strategy}' is not supported by the initial runtime.");
        }
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
