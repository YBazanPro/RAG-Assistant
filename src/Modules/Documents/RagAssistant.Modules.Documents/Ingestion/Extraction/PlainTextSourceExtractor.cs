using RagAssistant.Modules.Documents.Domain;

namespace RagAssistant.Modules.Documents.Ingestion.Extraction;

/// <summary>Plain-text versions (spec 007): one section without a path, paragraphs cut at blank lines, no Markdown recognised.</summary>
internal sealed class PlainTextSourceExtractor : ISourceExtractor
{
    public SourceFormat Format => SourceFormat.PlainText;

    public Task<SourceExtraction> ExtractAsync(VersionSource source, Action<int, int>? onProgress, CancellationToken cancellationToken)
    {
        var text = source.Content ?? throw new InvalidOperationException("The version has no text content.");
        return Task.FromResult(new SourceExtraction(Parse(text), $"{text.Length} characters"));
    }

    public static SourceDocument Parse(string content) =>
        new([new SourceSection([], null, LineBlocks.Parse(content.Split('\n'), markdown: false))]);
}
