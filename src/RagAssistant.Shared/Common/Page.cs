using System.Globalization;
using System.Text.Json.Serialization;
using FluentValidation;

namespace RagAssistant.Shared.Common;

/// <summary>Offset-paging envelope returned by every list endpoint (spec 008).</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, [property: JsonPropertyName("page")] int Number, int PageSize, int TotalCount);

/// <summary>
/// Parsing and validation of the <c>page</c> / <c>pageSize</c> query values. Endpoints bind them as raw strings so that
/// a malformed value is a <c>validation.failed</c> field error instead of a binding failure.
/// </summary>
public static class PagingRules
{
    public static int ParsePage(string? raw) => TryParse(raw, out var value) ? value : 1;

    public static int ParsePageSize(string? raw, int defaultSize) =>
        string.IsNullOrWhiteSpace(raw) ? defaultSize : TryParse(raw, out var value) ? value : defaultSize;

    public static int Skip(int page, int pageSize) => (int)Math.Min((long)(page - 1) * pageSize, int.MaxValue);

    public static void AddPagingRules<T>(this AbstractValidator<T> validator, Func<T, string?> page, Func<T, string?> pageSize, int maxPageSize)
    {
        validator.RuleFor(x => page(x))
            .Must(v => string.IsNullOrWhiteSpace(v) || (TryParse(v, out var n) && n >= 1))
            .WithMessage("'page' must be a whole number of at least 1.")
            .OverridePropertyName("page");

        validator.RuleFor(x => pageSize(x))
            .Must(v => string.IsNullOrWhiteSpace(v) || (TryParse(v, out var n) && n >= 1 && n <= maxPageSize))
            .WithMessage($"'pageSize' must be a whole number between 1 and {maxPageSize}.")
            .OverridePropertyName("pageSize");
    }

    private static bool TryParse(string? raw, out int value) =>
        int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}
