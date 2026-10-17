using RagAssistant.Modules.Documents.Ingestion;
using RagAssistant.Modules.Documents.Ingestion.Extraction;

namespace RagAssistant.Modules.Documents.Tests;

internal sealed class PlainTextSourceExtractorTests
{
    [Test]
    public async Task Parse_text_isOneSectionWithoutPathCutAtBlankLines()
    {
        var document = PlainTextSourceExtractor.Parse("first line\nsecond line\n\n\nnext paragraph");

        var section = document.Sections.Single();
        await Assert.That(section.Path.Length).IsEqualTo(0);
        await Assert.That(section.HeadingLine).IsNull();
        await Assert.That(section.Blocks.Select(b => b.Text).ToArray()).IsEquivalentTo(["first line\nsecond line", "next paragraph"]);
    }

    [Test]
    public async Task Parse_markdownLookalikes_areParagraphs()
    {
        var document = PlainTextSourceExtractor.Parse("# hash\n| pipe |\n```\nfence");

        var block = document.Sections.Single().Blocks.Single();
        await Assert.That(block.Kind).IsEqualTo(BlockKind.Paragraph);
        await Assert.That(block.Lines.Count).IsEqualTo(4);
    }

    [Test]
    public async Task ExtractAsync_content_returnsACharacterSummaryWithoutPages()
    {
        var extraction = await new PlainTextSourceExtractor().ExtractAsync(new VersionSource("abc", null), null, CancellationToken.None);

        await Assert.That(extraction.Summary).IsEqualTo("3 characters");
        await Assert.That(extraction.PagesWithoutText).IsNull();
    }
}
