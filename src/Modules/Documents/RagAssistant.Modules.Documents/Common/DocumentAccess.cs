using RagAssistant.Modules.Documents.Domain;

namespace RagAssistant.Modules.Documents.Common;

/// <summary>The one visibility rule (spec 016, ADR 0009): a user reads a document when it is shared or they imported it.</summary>
internal static class DocumentAccess
{
    public static IQueryable<Document> ReadableBy(this IQueryable<Document> documents, string viewerSub)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(viewerSub);
        return documents.Where(d => d.Visibility == Visibility.Shared || d.CreatedBy == viewerSub);
    }

    /// <summary>Parses the API spelling (case-insensitive); null or blank is "not given".</summary>
    public static bool TryParseVisibility(string? text, out Visibility? visibility)
    {
        visibility = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        switch (text.Trim().ToLowerInvariant())
        {
            case "shared":
                visibility = Visibility.Shared;
                return true;
            case "private":
                visibility = Visibility.Private;
                return true;
            default:
                return false;
        }
    }

    public static bool IsOwner(this Document document, string viewerSub) => document.CreatedBy == viewerSub;
}
