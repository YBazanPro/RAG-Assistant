using System.Globalization;

namespace RagAssistant.Web.Client.Documents;

public static class Formatting
{
    /// <summary>"English", "French", or the code itself; the UI stays English (spec 018).</summary>
    public static string LanguageName(string code) => code switch
    {
        "en" => "English",
        "fr" => "French",
        _ => code,
    };

    /// <summary>Timestamps are shown in the viewer's local time; <see cref="UtcTooltip"/> carries the exact UTC value.</summary>
    public static string Local(DateTimeOffset value) =>
        value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public static string UtcTooltip(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    public static string Date(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KiB",
        _ => $"{bytes / (1024.0 * 1024.0):0.#} MiB",
    };

    public static string FormatLabel(string format) => format switch
    {
        "pdf" => "PDF",
        "markdown" => "Markdown",
        "plaintext" => "Plain text",
        _ => format,
    };

    /// <summary>"p. 12" for one page, "pp. 12–13" for a range.</summary>
    public static string PageLabel(int start, int end) => end > start ? $"pp. {start}–{end}" : $"p. {start}";

    /// <summary>Runs of consecutive pages joined: 31, 32, 33, 40 becomes "31–33, 40".</summary>
    public static string PageRanges(IEnumerable<int> pages)
    {
        var ranges = new List<string>();
        int? start = null;
        var previous = 0;
        foreach (var page in pages.Order().Distinct())
        {
            if (start is null)
            {
                start = previous = page;
            }
            else if (page == previous + 1)
            {
                previous = page;
            }
            else
            {
                ranges.Add(Range(start.Value, previous));
                start = previous = page;
            }
        }

        if (start is not null)
        {
            ranges.Add(Range(start.Value, previous));
        }

        return string.Join(", ", ranges);

        static string Range(int from, int to) => to > from ? $"{from}–{to}" : $"{from}";
    }

    /// <summary>The warning for pages the text layer does not cover, or null when there are none.</summary>
    public static string? PagesWithoutTextWarning(int[]? pages) => pages is { Length: > 0 }
        ? pages.Length == 1
            ? $"Page {pages[0]} has no extractable text and is not searchable."
            : $"Pages {PageRanges(pages)} have no extractable text and are not searchable."
        : null;

    /// <summary>"Progress as text": embedding counts passages, extraction counts pages.</summary>
    public static string ProgressLabel(string stage, int current, int total) => stage switch
    {
        "embedding" => $"Embedding {current} of {total} passages",
        "extracting" => $"Extracting {current} of {total} pages",
        _ => $"{StageLabel(stage)} {current} of {total}",
    };

    public static string StatusLabel(string status) => status switch
    {
        "active" => "Active",
        "queued" => "Queued",
        "processing" => "Processing",
        "failed" => "Failed",
        "archived" => "Archived",
        _ => status,
    };

    public static string StageLabel(string stage) => stage switch
    {
        "queued" => "Queued",
        "extracting" => "Extracting",
        "chunking" => "Chunking",
        "embedding" => "Embedding",
        "storing" => "Storing",
        "completed" => "Completed",
        _ => stage,
    };
}
