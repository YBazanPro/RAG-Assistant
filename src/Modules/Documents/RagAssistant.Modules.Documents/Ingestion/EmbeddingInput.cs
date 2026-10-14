namespace RagAssistant.Modules.Documents.Ingestion;

internal static class EmbeddingInput
{
    /// <summary>The text that is embedded for a passage: its heading chain gives the model the context the passage text lacks.</summary>
    public static string Build(IReadOnlyList<string> sectionPath, string text) =>
        sectionPath.Count == 0 ? text : $"{string.Join(" > ", sectionPath)}\n\n{text}";
}
