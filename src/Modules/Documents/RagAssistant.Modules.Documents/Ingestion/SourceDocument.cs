namespace RagAssistant.Modules.Documents.Ingestion;

/// <summary>
/// What every extractor produces and the only input of the <see cref="Chunker"/>: the text of a version in reading order, cut into
/// sections and blocks. Block text is Markdown-flavoured whatever the source format: a code block keeps its fences and a table is
/// pipe-table lines, because the Chunker re-fences split code and repeats table headers.
/// </summary>
internal sealed record SourceDocument(IReadOnlyList<SourceSection> Sections);

/// <summary>
/// The blocks under one heading. Two sections may share a path (the same heading twice), so boundaries are explicit rather than
/// derived from path changes.
/// </summary>
/// <param name="Path">The heading chain, outermost first; empty before the first heading and for formats without headings.</param>
/// <param name="HeadingLine">The heading as written in the source, put back in front of the section's text when it merges into the previous passage; null when the source has none to repeat.</param>
internal sealed record SourceSection(string[] Path, string? HeadingLine, IReadOnlyList<SourceBlock> Blocks);

/// <param name="Page">1-based page the block was found on, 0 when the source has no pages.</param>
internal sealed record SourceBlock(BlockKind Kind, IReadOnlyList<string> Lines, int Page = 0)
{
    public string Text => string.Join('\n', Lines);
}

internal enum BlockKind
{
    Paragraph,

    /// <summary>A fenced code block, fences included; split pieces are re-fenced.</summary>
    Code,

    /// <summary>A pipe table; split pieces repeat the header rows.</summary>
    Table,
}
