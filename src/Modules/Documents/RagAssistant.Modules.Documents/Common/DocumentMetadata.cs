using System.Globalization;
using RagAssistant.Modules.Documents.Domain;

namespace RagAssistant.Modules.Documents.Common;

internal static class DocumentMetadata
{
    public const string DefaultLanguage = "en";
    public const string DateFormat = "yyyy-MM-dd";

    public static bool TryGetFormat(string fileName, out SourceFormat format)
    {
        switch (Path.GetExtension(fileName).ToLowerInvariant())
        {
            case ".md":
            case ".markdown":
                format = SourceFormat.Markdown;
                return true;
            case ".txt":
                format = SourceFormat.PlainText;
                return true;
            case ".pdf":
                format = SourceFormat.Pdf;
                return true;
            default:
                format = default;
                return false;
        }
    }

    /// <summary>Text formats keep the small limit; only a PDF may be large (spec 010).</summary>
    public static int MaxBytes(SourceFormat format, Options.ImportOptions options) =>
        format == SourceFormat.Pdf ? options.MaxPdfSizeBytes : options.MaxFileSizeBytes;

    /// <summary>Keeps only the last path segment, whichever separator the client used.</summary>
    public static string SanitizeFileName(string fileName)
    {
        var index = fileName.LastIndexOfAny(['/', '\\']);
        return index < 0 ? fileName : fileName[(index + 1)..];
    }

    public static string[] NormalizeTags(IEnumerable<string>? tags) =>
        tags is null ? [] : [.. tags.Select(t => t.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal)];

    public static string NormalizeLanguage(string? language) =>
        string.IsNullOrWhiteSpace(language) ? DefaultLanguage : language.Trim().ToLowerInvariant();

    public static bool IsValidLanguage(string language) =>
        language.Length == 2 && language.All(char.IsAsciiLetterLower);

    public static bool TryParseDate(string? value, out DateOnly? date)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            date = null;
            return true;
        }

        if (DateOnly.TryParseExact(value.Trim(), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            date = parsed;
            return true;
        }

        date = null;
        return false;
    }
}
