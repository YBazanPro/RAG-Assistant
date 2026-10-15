using RagAssistant.Modules.Documents.Options;
using RagAssistant.Modules.Documents.Pdf;
using static RagAssistant.Modules.Documents.Tests.TestPdf;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace RagAssistant.Modules.Documents.Tests;

// Spec 010, AC-14a to AC-14e: telling text PDFs from scans at upload.
internal sealed class PdfProbeTests
{
    private static PdfProbeResult Probe(byte[] pdf, PdfOptions? options = null, TimeProvider? clock = null, CancellationToken cancellationToken = default) =>
        new PdfProbe(MsOptions.Create(options ?? new PdfOptions()), clock ?? TimeProvider.System).Probe(new MemoryStream(pdf), cancellationToken);

    private static PageSpec Text(int n = 0) => Page(Body(700, $"A page that carries a real text layer, number {n}."));

    [Test]
    public async Task Probe_onlyFullPageImages_needsOcr()
    {
        var result = Probe(Create(Enumerable.Range(0, 4).Select(_ => ScannedPage())));

        await Assert.That(result.Kind).IsEqualTo(PdfKind.ScannedNeedsOcr);
        await Assert.That(result.PagesWithText).IsEqualTo(0);
        await Assert.That(result.ScannedLikePages).IsEqualTo(4);
    }

    [Test]
    public async Task Probe_everyPageHasText_isTextLayer()
    {
        var result = Probe(Create(Enumerable.Range(0, 5).Select(Text)));

        await Assert.That(result.Kind).IsEqualTo(PdfKind.TextLayer);
        await Assert.That(result.PagesSampled).IsEqualTo(5);
    }

    [Test]
    public async Task Probe_textFollowedByScans_isMixedAndSamplesBeyondTheFirstPages()
    {
        // 30 text pages then 10 scans: the first 10 sampled pages are all text, so only the spread sample can see the scans.
        var pages = Enumerable.Range(0, 30).Select(Text).Concat(Enumerable.Range(0, 10).Select(_ => ScannedPage()));

        var result = Probe(Create(pages));

        await Assert.That(result.Kind).IsEqualTo(PdfKind.Mixed);
        await Assert.That(result.PagesSampled).IsEqualTo(20);
        await Assert.That(result.ScannedLikePages).IsGreaterThan(0);
    }

    [Test]
    public async Task Probe_blankPageWithoutAnImage_isTextlessButNotScannedLike()
    {
        var result = Probe(Create([Text(1), Text(2), Text(3), BlankPage()]));

        await Assert.That(result.Kind).IsEqualTo(PdfKind.Mixed);
        await Assert.That(result.PagesWithText).IsEqualTo(3);
        await Assert.That(result.ScannedLikePages).IsEqualTo(0);
    }

    [Test]
    public async Task Probe_oneScannedPageAmongText_countsItAsScannedLike()
    {
        var result = Probe(Create([Text(1), Text(2), Text(3), ScannedPage()]));

        await Assert.That(result.Kind).IsEqualTo(PdfKind.Mixed);
        await Assert.That(result.ScannedLikePages).IsEqualTo(1);
    }

    [Test]
    public async Task Probe_textLayerOfSymbols_isUnusable()
    {
        var pdf = Create(Page(Body(700, "+=<>~|^$ +=<>~|^$ +=<>~|^$ +=<>~|^$ +=<>~|^$")));

        await Assert.That(Probe(pdf).Kind).IsEqualTo(PdfKind.UnusableText);
    }

    [Test]
    public async Task Probe_garbageThresholdIsConfigurable()
    {
        var pdf = Create(Page(Body(700, "Readable words in a line +=<>~ and more readable words follow here.")));

        await Assert.That(Probe(pdf).Kind).IsEqualTo(PdfKind.TextLayer);
        await Assert.That(Probe(pdf, new PdfOptions { MaxGarbageRatio = 0.01 }).Kind).IsEqualTo(PdfKind.UnusableText);
    }

    [Test]
    public async Task Probe_corruptEncryptedOrNotAPdf_isInconclusive()
    {
        await Assert.That(Probe(Corrupt()).Kind).IsEqualTo(PdfKind.Inconclusive);
        await Assert.That(Probe(Encrypted()).Kind).IsEqualTo(PdfKind.Inconclusive);
        await Assert.That(Probe([]).Kind).IsEqualTo(PdfKind.Inconclusive);
    }

    [Test]
    public async Task Probe_paddedPseudoPdf_isInconclusive()
    {
        var padded = new byte[4096];
        "%PDF-1.7\n"u8.CopyTo(padded);

        await Assert.That(Probe(padded).Kind).IsEqualTo(PdfKind.Inconclusive);
    }

    [Test]
    public async Task Probe_hugeFileThatOnlyStartsLikeAPdf_returnsAtOnceWithoutParsing()
    {
        var junk = new byte[30 * 1024 * 1024];
        "%PDF-1.7\n"u8.CopyTo(junk);
        var started = System.Diagnostics.Stopwatch.StartNew();

        var result = Probe(junk);

        await Assert.That(result.Kind).IsEqualTo(PdfKind.Inconclusive);
        await Assert.That(started.Elapsed).IsLessThan(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task ProbeAsync_waitExpires_isInconclusiveWithoutWaitingForTheParser()
    {
        var probe = new PdfProbe(MsOptions.Create(new PdfOptions()), new ImmediateTimeProvider());

        var result = await probe.ProbeAsync(new MemoryStream(Create(Enumerable.Range(0, 3).Select(Text))), CancellationToken.None);

        await Assert.That(result.Kind).IsEqualTo(PdfKind.Inconclusive);
    }

    [Test]
    public async Task ProbeAsync_textPdf_isClassified()
    {
        var probe = new PdfProbe(MsOptions.Create(new PdfOptions()), TimeProvider.System);

        var result = await probe.ProbeAsync(new MemoryStream(Create(Enumerable.Range(0, 3).Select(Text))), CancellationToken.None);

        await Assert.That(result.Kind).IsEqualTo(PdfKind.TextLayer);
    }

    [Test]
    public async Task Probe_timeout_isInconclusiveNotAnError()
    {
        var result = Probe(Create(Enumerable.Range(0, 3).Select(Text)), clock: new ImmediateTimeProvider());

        await Assert.That(result.Kind).IsEqualTo(PdfKind.Inconclusive);
    }

    [Test]
    public async Task Probe_callerCancels_throws()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.That(() => Probe(Create(Text()), cancellationToken: cts.Token)).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task SamplePages_longDocument_readsTheFirstPagesAndAnEvenSpreadOverTheRest()
    {
        var pages = PdfProbe.SamplePages(500, 10);

        await Assert.That(pages.Count).IsEqualTo(20);
        await Assert.That(pages.Take(10).ToArray()).IsEquivalentTo(Enumerable.Range(1, 10).ToArray());
        await Assert.That(pages.Skip(10).All(p => p is > 10 and <= 500)).IsTrue();
        await Assert.That(pages.Distinct().Count()).IsEqualTo(20);
        await Assert.That(pages.Skip(10).ToArray()).IsEquivalentTo(pages.Skip(10).Order().ToArray());
    }

    [Test]
    [Arguments(3, 10, 3)]
    [Arguments(10, 10, 10)]
    [Arguments(12, 10, 12)]
    [Arguments(20, 10, 20)]
    [Arguments(21, 10, 20)]
    [Arguments(1, 10, 1)]
    public async Task SamplePages_shortDocument_isReadCompletely(int pageCount, int probePages, int expected)
    {
        var pages = PdfProbe.SamplePages(pageCount, probePages);

        await Assert.That(pages.Count).IsEqualTo(expected);
        await Assert.That(pages.Distinct().Count()).IsEqualTo(expected);
        await Assert.That(pages.All(p => p >= 1 && p <= pageCount)).IsTrue();
    }

    /// <summary>A clock whose timers fire the moment they are created, so every timeout has already expired.</summary>
    private sealed class ImmediateTimeProvider : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                callback(state);
            }

            return new NoTimer();
        }

        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
