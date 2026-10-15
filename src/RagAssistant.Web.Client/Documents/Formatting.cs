using System.Globalization;

namespace RagAssistant.Web.Client.Documents;

public static class Formatting
{
    /// <summary>Timestamps are shown in the viewer's local time; <see cref="UtcTooltip"/> carries the exact UTC value.</summary>
    public static string Local(DateTimeOffset value) =>
        value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public static string UtcTooltip(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    public static string Date(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Size(int bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KiB",
        _ => $"{bytes / (1024.0 * 1024.0):0.#} MiB",
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
