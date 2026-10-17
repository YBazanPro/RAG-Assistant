using System.Globalization;

namespace RagAssistant.Web.Client.Documents;

/// <summary>The list page's state: what the URL carries and what the API receives (spec 008, "Routes and state").</summary>
public sealed record DocumentQuery
{
    public static readonly string[] Statuses = ["active", "queued", "processing", "failed", "archived"];

    public static readonly string[] Sorts = ["title", "-title", "updated", "-updated", "effectiveDate", "-effectiveDate"];

    public static readonly string[] Visibilities = ["mine", "shared"];

    public const string DefaultSort = "-updated";
    public const int MaxSearchLength = 200;
    public const int MaxTags = 20;

    public string? Q { get; init; }

    public string? Status { get; init; }

    public string? Language { get; init; }

    public string? Owner { get; init; }

    /// <summary><c>mine</c> (my private documents) or <c>shared</c>; null for everything I can read (spec 016).</summary>
    public string? Visibility { get; init; }

    public string[] Tags { get; init; } = [];

    public string? EffectiveFrom { get; init; }

    public string? EffectiveTo { get; init; }

    public string Sort { get; init; } = DefaultSort;

    public int Page { get; init; } = 1;

    public bool HasFilters =>
        !string.IsNullOrEmpty(Q) || Status is not null || Language is not null || Owner is not null || Visibility is not null
        || Tags.Length > 0 || EffectiveFrom is not null || EffectiveTo is not null;

    /// <summary>Builds a query from raw URL values, dropping every invalid one so a hand-edited URL still shows a list.</summary>
    public static DocumentQuery FromRaw(
        string? q, string? status, string? language, string? owner, IEnumerable<string>? tags,
        string? effectiveFrom, string? effectiveTo, string? sort, string? page, string? visibility = null)
    {
        var from = ValidDate(effectiveFrom);
        var to = ValidDate(effectiveTo);
        if (from is not null && to is not null && string.CompareOrdinal(from, to) > 0)
        {
            from = null;
            to = null;
        }

        var search = q?.Trim();
        return new DocumentQuery
        {
            Q = string.IsNullOrEmpty(search) || search.Length > MaxSearchLength ? null : search,
            Status = status is not null && Statuses.Contains(status) ? status : null,
            Language = language is { Length: 2 } && language.All(char.IsAsciiLetter) ? language.ToLowerInvariant() : null,
            Owner = string.IsNullOrWhiteSpace(owner) ? null : owner,
            Visibility = visibility is not null && Visibilities.Contains(visibility) ? visibility : null,
            Tags = [.. (tags ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim().ToLowerInvariant()).Distinct().Take(MaxTags)],
            EffectiveFrom = from,
            EffectiveTo = to,
            Sort = sort is not null && Sorts.Contains(sort) ? sort : DefaultSort,
            Page = int.TryParse(page, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1 ? n : 1,
        };
    }

    /// <summary>Name/value pairs in the order the URL and the API both use; defaults are omitted.</summary>
    public IEnumerable<KeyValuePair<string, string>> ToPairs()
    {
        if (!string.IsNullOrEmpty(Q))
        {
            yield return new("q", Q);
        }

        if (Status is not null)
        {
            yield return new("status", Status);
        }

        if (Language is not null)
        {
            yield return new("language", Language);
        }

        if (Owner is not null)
        {
            yield return new("owner", Owner);
        }

        if (Visibility is not null)
        {
            yield return new("visibility", Visibility);
        }

        foreach (var tag in Tags)
        {
            yield return new("tags", tag);
        }

        if (EffectiveFrom is not null)
        {
            yield return new("effectiveFrom", EffectiveFrom);
        }

        if (EffectiveTo is not null)
        {
            yield return new("effectiveTo", EffectiveTo);
        }

        if (Sort != DefaultSort)
        {
            yield return new("sort", Sort);
        }

        if (Page > 1)
        {
            yield return new("page", Page.ToString(CultureInfo.InvariantCulture));
        }
    }

    public string ToQueryString() =>
        string.Join('&', ToPairs().Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));

    /// <summary>Any filter, search or sort change starts again from the first page.</summary>
    public DocumentQuery WithFirstPage() => this with { Page = 1 };

    private static string? ValidDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : null;
}

/// <summary>A filter-bar edit; <see cref="Replace"/> is set for typing so the history is not flooded.</summary>
public sealed record FilterChange(DocumentQuery Query, bool Replace);

/// <summary>An option of a filter-bar select.</summary>
public sealed record FilterChoice(string Value, string Text);
