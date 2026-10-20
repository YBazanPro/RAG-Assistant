namespace RagAssistant.Modules.Documents.Ingestion;

internal static class EmbeddingInput
{
    /// <summary>
    /// Recorded on every indexed version (spec 021): 1 = heading path and text, 2 = document title, heading path and text.
    /// Raise it whenever <see cref="Build"/> changes, so versions embedded the old way show as needing reprocessing.
    /// </summary>
    public const int Version = 2;

    /// <summary>
    /// The text that is embedded for a passage: the document title and its heading chain give the model the context the passage
    /// text lacks (spec 021). A first heading that repeats the title is not written twice. The title is the one at ingestion time.
    /// </summary>
    public static string Build(string title, IReadOnlyList<string> sectionPath, string text)
    {
        var trimmed = title.Trim();
        IEnumerable<string> context = sectionPath.Count > 0 && string.Equals(sectionPath[0].Trim(), trimmed, StringComparison.OrdinalIgnoreCase)
            ? sectionPath
            : [trimmed, .. sectionPath];
        var heading = string.Join(" > ", context.Where(h => h.Length > 0));
        return heading.Length == 0 ? text : $"{heading}\n\n{text}";
    }
}
