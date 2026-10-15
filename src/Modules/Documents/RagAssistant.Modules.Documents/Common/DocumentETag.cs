using System.Globalization;

namespace RagAssistant.Modules.Documents.Common;

/// <summary>The document's strong ETag is its metadata revision in quotes; weak tags and <c>*</c> are not supported (spec 009).</summary>
internal static class DocumentETag
{
    public static string Format(int revision) => $"\"{revision.ToString(CultureInfo.InvariantCulture)}\"";

    public static bool TryParse(string? header, out int revision)
    {
        revision = 0;
        return header is { Length: > 2 } h
            && h[0] == '"' && h[^1] == '"'
            && int.TryParse(h.AsSpan(1, h.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out revision);
    }
}
