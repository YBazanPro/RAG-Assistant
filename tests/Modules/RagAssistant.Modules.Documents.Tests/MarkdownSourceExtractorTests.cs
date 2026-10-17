using RagAssistant.Modules.Documents.Ingestion;
using RagAssistant.Modules.Documents.Ingestion.Extraction;

namespace RagAssistant.Modules.Documents.Tests;

internal sealed class MarkdownSourceExtractorTests
{
    [Test]
    public async Task Parse_headings_openSectionsWithPathAndHeadingLine()
    {
        var document = MarkdownSourceExtractor.Parse("intro\n# A\nbody a\n## B  \nbody b");

        await Assert.That(document.Sections.Count).IsEqualTo(3);
        await Assert.That(document.Sections[0].Path.Length).IsEqualTo(0);
        await Assert.That(document.Sections[0].HeadingLine).IsNull();
        await Assert.That(document.Sections[1].Path).IsEquivalentTo(["A"]);
        await Assert.That(document.Sections[1].HeadingLine).IsEqualTo("# A");
        await Assert.That(document.Sections[2].Path).IsEquivalentTo(["A", "B"]);
        await Assert.That(document.Sections[2].HeadingLine).IsEqualTo("## B");
        await Assert.That(document.Sections[2].Blocks.Single().Text).IsEqualTo("body b");
    }

    [Test]
    public async Task Parse_headingWithoutBody_keepsAnEmptySection()
    {
        var document = MarkdownSourceExtractor.Parse("# A\n## B\nbody");

        await Assert.That(document.Sections.Select(s => s.Blocks.Count).ToArray()).IsEquivalentTo([0, 0, 1]);
        await Assert.That(document.Sections[2].Path).IsEquivalentTo(["A", "B"]);
    }

    [Test]
    public async Task Parse_sameHeadingTwice_givesTwoSections()
    {
        var document = MarkdownSourceExtractor.Parse("# A\none\n# A\ntwo");

        var withA = document.Sections.Where(s => s.Path is ["A"]).ToList();
        await Assert.That(withA.Count).IsEqualTo(2);
        await Assert.That(withA.Select(s => s.Blocks.Single().Text).ToArray()).IsEquivalentTo(["one", "two"]);
    }

    [Test]
    public async Task Parse_fencedCode_isOneCodeBlockWithFencesAndBlankLines()
    {
        var document = MarkdownSourceExtractor.Parse("before\n```sh\n# not a heading\n\necho hi\n```\nafter");

        var blocks = document.Sections.Single().Blocks;
        await Assert.That(blocks.Select(b => b.Kind).ToArray()).IsEquivalentTo([BlockKind.Paragraph, BlockKind.Code, BlockKind.Paragraph]);
        await Assert.That(blocks[1].Text).IsEqualTo("```sh\n# not a heading\n\necho hi\n```");
    }

    [Test]
    public async Task Parse_pipeTable_isOneTableBlock()
    {
        var document = MarkdownSourceExtractor.Parse("| a | b |\n|---|---|\n| 1 | 2 |\ntext");

        var blocks = document.Sections.Single().Blocks;
        await Assert.That(blocks.Select(b => b.Kind).ToArray()).IsEquivalentTo([BlockKind.Table, BlockKind.Paragraph]);
        await Assert.That(blocks[0].Lines.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Parse_blocks_haveNoPage()
    {
        var document = MarkdownSourceExtractor.Parse("# A\none\n\ntwo");

        await Assert.That(document.Sections.SelectMany(s => s.Blocks).All(b => b.Page == 0)).IsTrue();
    }

    [Test]
    public async Task ExtractAsync_content_returnsTheDocumentAndACharacterSummary()
    {
        var extraction = await new MarkdownSourceExtractor().ExtractAsync(new VersionSource("# A\nbody", null), null, CancellationToken.None);

        await Assert.That(extraction.Summary).IsEqualTo("8 characters");
        await Assert.That(extraction.PagesWithoutText).IsNull();
        await Assert.That(extraction.Document.Sections[^1].Path).IsEquivalentTo(["A"]);
    }

    [Test]
    public async Task ExtractAsync_withoutContent_throws()
    {
        await Assert.That(async () => { await new MarkdownSourceExtractor().ExtractAsync(new VersionSource(null, null), null, CancellationToken.None); })
            .Throws<InvalidOperationException>();
    }
}
