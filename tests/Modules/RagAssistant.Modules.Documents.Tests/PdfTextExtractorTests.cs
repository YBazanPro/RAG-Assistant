using RagAssistant.Modules.Documents.Ingestion;
using RagAssistant.Modules.Documents.Options;
using RagAssistant.Modules.Documents.Pdf;
using static RagAssistant.Modules.Documents.Tests.TestPdf;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace RagAssistant.Modules.Documents.Tests;

// Spec 010, AC-9 to AC-15 and AC-13a: text extraction from generated PDFs.
internal sealed class PdfTextExtractorTests
{
    private static PdfExtraction Extract(byte[] pdf, PdfOptions? options = null, CancellationToken cancellationToken = default) =>
        new PdfTextExtractor(MsOptions.Create(options ?? new PdfOptions())).Extract(new MemoryStream(pdf), onPageDone: null, cancellationToken);

    private static IngestionException Fails(byte[] pdf, PdfOptions? options = null)
    {
        try
        {
            Extract(pdf, options);
        }
        catch (IngestionException ex)
        {
            return ex;
        }

        throw new InvalidOperationException("Expected the extraction to fail.");
    }

    private static string AllText(PdfExtraction extraction) => string.Join("\n\n", extraction.Blocks.Select(b => b.Text));

    [Test]
    public async Task Extract_twoTextPages_returnsParagraphsInOrderWithTheirPages()
    {
        var pdf = Create(
            Page(Body(700, "The quarterly budget review covers travel expenses.", "Approvals above the limit need a second signature.")),
            Page(Body(700, "Equipment purchases follow the procurement policy.")));

        var result = Extract(pdf);

        await Assert.That(result.PageCount).IsEqualTo(2);
        await Assert.That(result.Blocks.Select(b => b.Page).ToArray()).IsEquivalentTo([1, 2]);
        await Assert.That(result.Blocks[0].Text).IsEqualTo("The quarterly budget review covers travel expenses. Approvals above the limit need a second signature.");
        await Assert.That(result.Blocks[1].Text).IsEqualTo("Equipment purchases follow the procurement policy.");
        await Assert.That(result.PagesWithoutText.Length).IsEqualTo(0);
    }

    [Test]
    public async Task Extract_thenChunk_aPassageSpanningTwoPagesHasBothPages()
    {
        var pdf = Create(
            Page(Body(700, "The quarterly budget review covers travel expenses.")),
            Page(Body(700, "Equipment purchases follow the procurement policy.")));

        var passages = Chunker.Chunk(Extract(pdf).Blocks, new ChunkingOptions { MinTokens = 1 });

        await Assert.That(passages.Count).IsEqualTo(1);
        await Assert.That(passages[0].PageStart).IsEqualTo(1);
        await Assert.That(passages[0].PageEnd).IsEqualTo(2);
        await Assert.That(passages[0].Text).IsEqualTo(
            "The quarterly budget review covers travel expenses.\n\nEquipment purchases follow the procurement policy.");
    }

    [Test]
    public async Task Extract_withAnOutline_followsItsLevelsAndFoldsBeyondThree()
    {
        var pdf = Create(
            [
                Page([new Item("Some preface text sits before the first chapter.", 50, 790), new Item("Chapter 1", 50, 760), .. Body(740, "Body of chapter one runs for a few words here.")]),
                Page([new Item("Section 1.1", 50, 790), .. Body(770, "Body of section one point one is right here.")]),
                Page([new Item("Detail", 50, 790), .. Body(770, "Body of the detail level is written right here.")]),
                Page([new Item("Fine print", 50, 790), .. Body(770, "Body of the fourth level is written right here.")]),
            ],
            b => b.Bookmarks = Outline(Entry("Chapter 1", 0, 1, Entry("Section 1.1", 1, 2, Entry("Detail", 2, 3, Entry("Fine print", 3, 4))))));

        var result = Extract(pdf);

        var byText = result.Blocks.ToDictionary(b => b.Text, b => b.SectionPath);
        await Assert.That(byText["Some preface text sits before the first chapter."]).IsEquivalentTo(Array.Empty<string>());
        await Assert.That(byText["Body of chapter one runs for a few words here."]).IsEquivalentTo(["Chapter 1"]);
        await Assert.That(byText["Body of section one point one is right here."]).IsEquivalentTo(["Chapter 1", "Section 1.1"]);
        await Assert.That(byText["Body of the detail level is written right here."]).IsEquivalentTo(["Chapter 1", "Section 1.1", "Detail"]);
        await Assert.That(byText["Body of the fourth level is written right here."]).IsEquivalentTo(["Chapter 1", "Section 1.1", "Fine print"]);
        await Assert.That(AllText(result)).DoesNotContain("Chapter 1");
        await Assert.That(AllText(result)).DoesNotContain("Section 1.1");
    }

    [Test]
    public async Task Extract_outlineEntryWithoutAMatchingLine_startsAtTheTopOfItsPage()
    {
        var pdf = Create(
            [
                Page(Body(700, "The first page belongs to no section at all.")),
                Page(Body(700, "The second page starts the named section here.")),
            ],
            b => b.Bookmarks = Outline(Entry("Totally different title", 0, 2)));

        var result = Extract(pdf);

        await Assert.That(result.Blocks[0].SectionPath.Length).IsEqualTo(0);
        await Assert.That(result.Blocks[1].SectionPath).IsEquivalentTo(["Totally different title"]);
        await Assert.That(result.Blocks[1].Text).IsEqualTo("The second page starts the named section here.");
    }

    [Test]
    public async Task Extract_withoutAnOutline_usesLargerAndBoldLinesAsHeadings()
    {
        var pdf = Create(
            Page([
                new Item("Annual Report", 50, 780, 24, Bold: true),
                new Item("Overview", 50, 740, 16, Bold: true),
                .. Body(715, "Body text line one about the report overview section.", "Line two continues the overview body text right here."),
                new Item("Details", 50, 660, 16, Bold: true),
                .. Body(635, "Details body text line one is about the figures.")]));

        var result = Extract(pdf);

        await Assert.That(result.Blocks.Count).IsEqualTo(2);
        await Assert.That(result.Blocks[0].SectionPath).IsEquivalentTo(["Annual Report", "Overview"]);
        await Assert.That(result.Blocks[1].SectionPath).IsEquivalentTo(["Annual Report", "Details"]);
        await Assert.That(AllText(result)).DoesNotContain("Overview");
        await Assert.That(AllText(result)).DoesNotContain("Annual Report");
    }

    [Test]
    public async Task Extract_uniformBodyText_producesNoHeadings()
    {
        var pdf = Create(Page(Body(700, "One plain line of body text with the same font.", "Another plain line of body text in the same font.")));

        var result = Extract(pdf);

        await Assert.That(result.Blocks.Count).IsEqualTo(1);
        await Assert.That(result.Blocks[0].SectionPath.Length).IsEqualTo(0);
    }

    [Test]
    public async Task Extract_repeatedHeaderFooterAndPageNumbers_areRemovedAndCounted()
    {
        var pages = Enumerable.Range(1, 3).Select(n => Page([
            new Item("Acme Corp Confidential", 50, 815),
            .. Body(700, $"Body line for page {n} with some distinct words {n}{n}."),
            new Item($"Page {n} of 3", 50, 40),
            new Item($"{n}", 300, 20)])).ToList();
        var pdf = Create(pages);

        var result = Extract(pdf);

        await Assert.That(AllText(result)).DoesNotContain("Acme");
        await Assert.That(AllText(result)).DoesNotContain("Page 1 of");
        await Assert.That(result.MarginLinesRemoved).IsEqualTo(9);
        await Assert.That(result.Blocks.Count).IsEqualTo(3);
        await Assert.That(result.Blocks[1].Text).IsEqualTo("Body line for page 2 with some distinct words 22.");
    }

    [Test]
    public async Task Extract_removeRepeatedMarginsOff_keepsThem()
    {
        var pages = Enumerable.Range(1, 3).Select(n => Page([
            new Item("Acme Corp Confidential", 50, 815),
            .. Body(700, $"Body line for page {n} with some distinct words {n}{n}.")])).ToList();

        var result = Extract(Create(pages), new PdfOptions { RemoveRepeatedMargins = false });

        await Assert.That(AllText(result)).Contains("Acme Corp Confidential");
        await Assert.That(result.MarginLinesRemoved).IsEqualTo(0);
    }

    [Test]
    public async Task Extract_singlePage_keepsItsFirstAndLastLine()
    {
        var pdf = Create(Page([new Item("Only header on a single page", 50, 815), .. Body(700, "The body of the only page has enough words.")]));

        var result = Extract(pdf);

        await Assert.That(AllText(result)).Contains("Only header on a single page");
    }

    [Test]
    public async Task Extract_twoColumns_readsTheLeftColumnBeforeTheRight()
    {
        var items = Enumerable.Range(0, 8)
            .SelectMany(i => new[]
            {
                new Item($"Left side line {i} with words", 50, 700 - (i * 14)),
                new Item($"Right side line {i} with words", 320, 700 - (i * 14)),
            })
            .ToArray();

        var text = AllText(Extract(Create(Page(items))));

        await Assert.That(text.IndexOf("Left side line 7", StringComparison.Ordinal)).IsLessThan(text.IndexOf("Right side line 0", StringComparison.Ordinal));
    }

    [Test]
    public async Task Extract_twoColumnsWithoutLayoutAnalysis_usesContentOrder()
    {
        var items = Enumerable.Range(0, 8)
            .SelectMany(i => new[]
            {
                new Item($"Left side line {i} with words", 50, 700 - (i * 14)),
                new Item($"Right side line {i} with words", 320, 700 - (i * 14)),
            })
            .ToArray();

        var text = AllText(Extract(Create(Page(items)), new PdfOptions { LayoutAnalysis = false }));

        await Assert.That(text.IndexOf("Right side line 0", StringComparison.Ordinal)).IsLessThan(text.IndexOf("Left side line 1", StringComparison.Ordinal));
    }

    [Test]
    public async Task Extract_hyphenatedLineBreak_joinsTheWord()
    {
        var pdf = Create(Page(Body(700, "The members signed an inter-", "national agreement on the first of May.")));

        var result = Extract(pdf);

        await Assert.That(result.Blocks[0].Text).IsEqualTo("The members signed an international agreement on the first of May.");
    }

    [Test]
    public async Task Extract_hyphenBeforeAnUppercaseWord_isKept()
    {
        var pdf = Create(Page(Body(700, "The report was written by the well-", "Known author of the earlier edition.")));

        var result = Extract(pdf);

        await Assert.That(result.Blocks[0].Text).Contains("well- Known");
    }

    [Test]
    public async Task Extract_largerVerticalGap_startsANewParagraph()
    {
        var pdf = Create(Page([
            .. Body(700, "First paragraph sentence one is right here.", "First paragraph sentence two follows on the next line."),
            .. Body(650, "Second paragraph begins after a larger vertical gap.")]));

        var result = Extract(pdf);

        await Assert.That(result.Blocks.Count).IsEqualTo(2);
        await Assert.That(result.Blocks[1].Text).IsEqualTo("Second paragraph begins after a larger vertical gap.");
    }

    [Test]
    public async Task Extract_pagesWithoutText_areListedAndTheRunProceeds()
    {
        var pages = new List<PageSpec> { Page(Body(700, "The single page that carries the text layer here.")) };
        pages.AddRange(Enumerable.Range(0, 9).Select(_ => ScannedPage()));

        var result = Extract(Create(pages));

        await Assert.That(result.PagesWithoutText).IsEquivalentTo(Enumerable.Range(2, 9).ToArray());
        await Assert.That(result.Blocks.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments(10, 1, true)]
    [Arguments(11, 1, false)]
    [Arguments(5, 0, false)]
    public async Task Extract_shareOfTextPages_decidesBetweenIndexingAndNeedingOcr(int pageCount, int textPages, bool indexed)
    {
        var pages = Enumerable.Range(0, pageCount)
            .Select(i => i < textPages ? Page(Body(700, "A page that carries a real text layer for tests.")) : ScannedPage())
            .ToList();

        if (indexed)
        {
            await Assert.That(Extract(Create(pages)).PageCount).IsEqualTo(pageCount);
        }
        else
        {
            await Assert.That(Fails(Create(pages)).Code).IsEqualTo("documents.requires_ocr");
        }
    }

    [Test]
    public async Task Extract_textLayerOfSymbols_failsAsUnusable()
    {
        var pdf = Create(Page(Body(700, "+=<>~|^$ +=<>~|^$ +=<>~|^$ +=<>~|^$ +=<>~|^$", "+=<>~|^$ +=<>~|^$ +=<>~|^$ +=<>~|^$ +=<>~|^$")));

        await Assert.That(Fails(pdf).Code).IsEqualTo("documents.text_layer_unusable");
    }

    [Test]
    public async Task Extract_encryptedPdf_failsWithPdfEncrypted()
    {
        var failure = Fails(Encrypted());

        await Assert.That(failure.Code).IsEqualTo("documents.pdf_encrypted");
    }

    [Test]
    public async Task Extract_corruptPdf_failsWithAContentFreeMessage()
    {
        var failure = Fails(Corrupt());

        await Assert.That(failure.Code).IsEqualTo("documents.pdf_invalid");
        await Assert.That(failure.Message).DoesNotContain("garbage");
    }

    [Test]
    public async Task Extract_moreThanMaxPages_failsBeforeReadingThem()
    {
        var pages = Enumerable.Range(0, 3).Select(_ => Page(Body(700, "Page text that is long enough to count here.")));

        var failure = Fails(Create(pages), new PdfOptions { MaxPages = 2 });

        await Assert.That(failure.Code).IsEqualTo("documents.pdf_too_many_pages");
    }

    [Test]
    public async Task Extract_cancelled_throwsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var pdf = Create(Page(Body(700, "Page text that is long enough to count here.")));

        await Assert.That(() => Extract(pdf, cancellationToken: cts.Token)).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task Extract_reportsProgressPerPage()
    {
        var pages = Enumerable.Range(0, 3).Select(_ => Page(Body(700, "Page text that is long enough to count here.")));
        var seen = new List<(int Done, int Total)>();

        new PdfTextExtractor(MsOptions.Create(new PdfOptions())).Extract(new MemoryStream(Create(pages)), (done, total) => seen.Add((done, total)), CancellationToken.None);

        await Assert.That(seen).IsEquivalentTo([(1, 3), (2, 3), (3, 3)]);
    }
}
