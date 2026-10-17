using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using RagAssistant.Modules.Documents.Common;
using RagAssistant.Modules.Documents.Ingestion;
using RagAssistant.Modules.Documents.Options;
using RagAssistant.Modules.Documents.Pdf;
using RagAssistant.Modules.Documents.Storage;
using static RagAssistant.Modules.Documents.Tests.TestPdf;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace RagAssistant.Modules.Documents.Tests;

internal sealed class PdfSourceExtractorTests
{
    private const string Key = "pdf/test.pdf";

    private static PdfSourceExtractor Create(IPdfTextExtractor parser, byte[]? file, PdfOptions? options = null)
    {
        var meters = new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>();
        var o = MsOptions.Create(options ?? new PdfOptions());
        return new PdfSourceExtractor(parser, new SingleFileStore(file), o, new DocumentsMetrics(meters), TimeProvider.System);
    }

    private static PdfSourceExtractor Create(byte[]? file) => Create(new PdfTextExtractor(MsOptions.Create(new PdfOptions())), file);

    [Test]
    public async Task ToDocument_consecutiveBlocksWithTheSamePath_shareASection()
    {
        var document = PdfSourceExtractor.ToDocument(
        [
            new("one", ["A"], 1),
            new("two", ["A"], 2),
            new("three", ["B"], 2),
            new("four", ["A"], 3),
        ]);

        await Assert.That(document.Sections.Select(s => string.Join('/', s.Path)).ToArray()).IsEquivalentTo(["A", "B", "A"]);
        await Assert.That(document.Sections[0].Blocks.Select(b => b.Page).ToArray()).IsEquivalentTo([1, 2]);
        await Assert.That(document.Sections.All(s => s.HeadingLine is null)).IsTrue();
    }

    [Test]
    public async Task ToDocument_whitespaceBlocks_areDroppedWithoutSplittingTheSection()
    {
        var document = PdfSourceExtractor.ToDocument([new("one", ["A"], 1), new("  ", ["B"], 1), new("two", ["A"], 2)]);

        await Assert.That(document.Sections.Count).IsEqualTo(1);
        await Assert.That(document.Sections[0].Blocks.Count).IsEqualTo(2);
    }

    [Test]
    public async Task ToDocument_lineBreaks_becomeLinesOfAParagraph()
    {
        var document = PdfSourceExtractor.ToDocument([new("first\nsecond", [], 4)]);

        var block = document.Sections.Single().Blocks.Single();
        await Assert.That(block.Kind).IsEqualTo(BlockKind.Paragraph);
        await Assert.That(block.Lines).IsEquivalentTo(["first", "second"]);
        await Assert.That(block.Page).IsEqualTo(4);
    }

    [Test]
    public async Task ExtractAsync_storedPdf_returnsBlocksSummaryPagesAndProgress()
    {
        var pdf = TestPdf.Create(
            Page(Body(700, "The quarterly budget review covers travel expenses.")),
            Page(Body(700, "Equipment purchases follow the procurement policy.")));
        var progress = new List<(int Done, int Total)>();

        var extraction = await Create(pdf).ExtractAsync(new VersionSource(null, Key), (done, total) => progress.Add((done, total)), CancellationToken.None);

        await Assert.That(extraction.Summary).IsEqualTo("2 pages, 0 without text, 0 margin lines removed");
        await Assert.That(extraction.PagesWithoutText).IsNotNull();
        await Assert.That(extraction.PagesWithoutText!.Length).IsEqualTo(0);
        await Assert.That(extraction.Document.Sections.SelectMany(s => s.Blocks).Select(b => b.Page).ToArray()).IsEquivalentTo([1, 2]);
        await Assert.That(progress[^1]).IsEqualTo((2, 2));
    }

    [Test]
    public async Task ExtractAsync_fileMissingFromTheStore_failsWithFileMissing()
    {
        var ex = await Assert.That(async () => { await Create(file: null).ExtractAsync(new VersionSource(null, Key), null, CancellationToken.None); })
            .Throws<IngestionException>();

        await Assert.That(ex!.Code).IsEqualTo(IngestionErrorCodes.FileMissing);
    }

    [Test]
    public async Task ExtractAsync_parserOverTheTimeout_failsWithExtractionTimeout()
    {
        var extractor = Create(new HangingParser(), [1], new PdfOptions { ExtractionTimeout = TimeSpan.FromMilliseconds(50) });

        var ex = await Assert.That(async () => { await extractor.ExtractAsync(new VersionSource(null, Key), null, CancellationToken.None); })
            .Throws<IngestionException>();

        await Assert.That(ex!.Code).IsEqualTo(IngestionErrorCodes.ExtractionTimeout);
    }

    private sealed class SingleFileStore(byte[]? file) : IOriginalFileStore
    {
        public Stream? OpenRead(string storageKey) => file is null || storageKey != Key ? null : new MemoryStream(file);

        public Task<StagedFile?> StageAsync(Stream content, long maxBytes, CancellationToken cancellationToken) => throw new NotSupportedException();

        public int DeleteStaleStagedFiles(TimeSpan age) => throw new NotSupportedException();
    }

    /// <summary>A parser stuck inside one call: it ignores the token, like PdfPig inside a slow page.</summary>
    private sealed class HangingParser : IPdfTextExtractor
    {
        public PdfExtraction Extract(Stream pdf, Action<int, int>? onPageDone, CancellationToken cancellationToken)
        {
            Thread.Sleep(TimeSpan.FromSeconds(2));
            return new PdfExtraction([], 0, [], 0);
        }
    }
}
