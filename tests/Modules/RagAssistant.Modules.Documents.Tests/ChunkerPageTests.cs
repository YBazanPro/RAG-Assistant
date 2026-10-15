using RagAssistant.Modules.Documents.Ingestion;
using RagAssistant.Modules.Documents.Options;

namespace RagAssistant.Modules.Documents.Tests;

// Spec 010, AC-9 and AC-17: PDF paragraphs go through the same packing code as text, and passages know their pages.
internal sealed class ChunkerPageTests
{
    private static readonly string[] NoPath = [];

    private static readonly ChunkingOptions Options = new() { MaxTokens = 50, OverlapTokens = 5, MinTokens = 1 };

    private static string Words(string label, int count) => string.Join(' ', Enumerable.Range(0, count).Select(i => $"{label}{i}"));

    [Test]
    public async Task Chunk_blocksOnTwoPagesPackedTogether_spanBothPages()
    {
        var passages = Chunker.Chunk([new("First paragraph.", NoPath, 1), new("Second paragraph.", NoPath, 2)], Options);

        await Assert.That(passages.Count).IsEqualTo(1);
        await Assert.That(passages[0].PageStart).IsEqualTo(1);
        await Assert.That(passages[0].PageEnd).IsEqualTo(2);
        await Assert.That(passages[0].Text).IsEqualTo("First paragraph.\n\nSecond paragraph.");
    }

    [Test]
    public async Task Chunk_blocksTooBigToShare_getTheirOwnPages()
    {
        var passages = Chunker.Chunk([new(Words("a", 40), NoPath, 3), new(Words("b", 40), NoPath, 4)], Options);

        await Assert.That(passages.Count).IsGreaterThanOrEqualTo(2);
        await Assert.That(passages[0].PageStart).IsEqualTo(3);
        await Assert.That(passages[0].PageEnd).IsEqualTo(3);
        await Assert.That(passages[^1].PageEnd).IsEqualTo(4);
    }

    [Test]
    public async Task Chunk_aLongBlockSplitIntoPieces_keepsItsPageOnEveryPiece()
    {
        var passages = Chunker.Chunk([new(Words("w", 200), NoPath, 7)], Options);

        await Assert.That(passages.Count).IsGreaterThan(1);
        await Assert.That(passages.All(p => p.PageStart == 7 && p.PageEnd == 7)).IsTrue();
    }

    [Test]
    public async Task Chunk_overlapFromThePreviousPassage_extendsTheStartPage()
    {
        var passages = Chunker.Chunk([new(Words("a", 40), NoPath, 1), new(Words("b", 40), NoPath, 2)], Options);

        var second = passages.First(p => p.Text.Contains("b0", StringComparison.Ordinal));
        await Assert.That(second.Text).Contains("a39");
        await Assert.That(second.PageStart).IsEqualTo(1);
        await Assert.That(second.PageEnd).IsEqualTo(2);
    }

    [Test]
    public async Task Chunk_differentSectionPaths_neverShareAPassage()
    {
        var passages = Chunker.Chunk([new("Intro text.", ["A"], 1), new("Other text.", ["B"], 1)], new ChunkingOptions { MaxTokens = 50, OverlapTokens = 5, MinTokens = 1 });

        await Assert.That(passages.Count).IsEqualTo(2);
        await Assert.That(passages[0].SectionPath).IsEquivalentTo(["A"]);
        await Assert.That(passages[1].SectionPath).IsEquivalentTo(["B"]);
    }

    [Test]
    public async Task Chunk_respectsTheTokenLimitLikeText()
    {
        var passages = Chunker.Chunk([new(Words("x", 300), NoPath, 1), new(Words("y", 300), NoPath, 2)], Options);

        await Assert.That(passages.All(p => p.TokenEstimate <= Options.MaxTokens)).IsTrue();
    }

    [Test]
    public async Task Chunk_textInput_hasNoPages()
    {
        var passages = Chunker.Chunk("# Title\n\nSome body text.", Domain.SourceFormat.Markdown, Options);

        await Assert.That(passages.All(p => p.PageStart is null && p.PageEnd is null)).IsTrue();
    }

    [Test]
    public async Task Chunk_emptyBlocks_areSkipped()
    {
        var passages = Chunker.Chunk([new("  ", NoPath, 1), new("Real text.", NoPath, 2)], Options);

        await Assert.That(passages.Count).IsEqualTo(1);
        await Assert.That(passages[0].PageStart).IsEqualTo(2);
    }
}
