namespace RagAssistant.Modules.Documents.Ingestion;

/// <summary>A paragraph of extracted text with the heading chain it sits under and the page it was found on (1-based).</summary>
internal sealed record SourceBlock(string Text, string[] SectionPath, int Page);
