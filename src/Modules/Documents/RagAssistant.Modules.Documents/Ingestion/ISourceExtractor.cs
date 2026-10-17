using RagAssistant.Modules.Documents.Domain;

namespace RagAssistant.Modules.Documents.Ingestion;

/// <summary>
/// Turns the stored source of a version into a <see cref="SourceDocument"/>; one implementation per <see cref="SourceFormat"/>.
/// Adding a format means adding an extractor: chunking, embedding and storing never look at the format.
/// </summary>
internal interface ISourceExtractor
{
    SourceFormat Format { get; }

    /// <param name="onProgress">Called with (done, total) during long extractions, possibly from another thread.</param>
    /// <exception cref="IngestionException">The source cannot be extracted; the code and message are shown to the user.</exception>
    Task<SourceExtraction> ExtractAsync(VersionSource source, Action<int, int>? onProgress, CancellationToken cancellationToken);
}

/// <summary>Where the source of a version lives: inline text for text formats, a file-store key for uploaded files.</summary>
internal sealed record VersionSource(string? Content, string? StorageKey);

/// <param name="Summary">The message of the completed extraction event.</param>
/// <param name="PagesWithoutText">Set for paged formats (empty when every page has text); the processor records it on the version.</param>
internal sealed record SourceExtraction(SourceDocument Document, string Summary, int[]? PagesWithoutText = null);
