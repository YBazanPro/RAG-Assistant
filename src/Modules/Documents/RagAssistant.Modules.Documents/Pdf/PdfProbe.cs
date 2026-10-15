using Microsoft.Extensions.Options;
using RagAssistant.Modules.Documents.Options;
using UglyToad.PdfPig;

namespace RagAssistant.Modules.Documents.Pdf;

internal enum PdfKind
{
    /// <summary>Every sampled page has a text layer.</summary>
    TextLayer,

    /// <summary>Some sampled pages have no text but enough do to be worth indexing.</summary>
    Mixed,

    /// <summary>Almost no page has text: the PDF looks scanned and needs OCR.</summary>
    ScannedNeedsOcr,

    /// <summary>There is a text layer but it is mostly replacement characters or symbols.</summary>
    UnusableText,

    /// <summary>The probe could not tell (unreadable file, encrypted, timeout); ingestion reports the specific failure.</summary>
    Inconclusive,
}

internal sealed record PdfProbeResult(PdfKind Kind, int PagesSampled, int PagesWithText, int ScannedLikePages);

/// <summary>Decides at upload whether a PDF has extractable text or needs OCR, from a bounded sample of its pages (spec 010).</summary>
internal interface IPdfProbe
{
    Task<PdfProbeResult> ProbeAsync(Stream pdf, CancellationToken cancellationToken);
}

internal sealed class PdfProbe(IOptions<PdfOptions> options, TimeProvider clock) : IPdfProbe
{
    private const int TrailerWindow = 4096;

    private static readonly PdfProbeResult Unknown = new(PdfKind.Inconclusive, 0, 0, 0);

    /// <summary>Runs the probe off the request thread and stops waiting for it after <c>ProbeTimeout</c>, whatever the parser is doing.</summary>
    public async Task<PdfProbeResult> ProbeAsync(Stream pdf, CancellationToken cancellationToken)
    {
        var work = Task.Run(() => Probe(pdf, cancellationToken), cancellationToken);
        try
        {
            return await work.WaitAsync(options.Value.ProbeTimeout, clock, cancellationToken);
        }
        catch (TimeoutException)
        {
            // The parser cannot be interrupted, so the abandoned task is left to finish on its own; its outcome no longer matters.
            _ = work.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return Unknown;
        }
    }

    public PdfProbeResult Probe(Stream pdf, CancellationToken cancellationToken)
    {
        var o = options.Value;
        if (!HasCrossReferenceTrailer(pdf))
        {
            // No "startxref" near the end: a damaged or fake PDF. Parsing it would only search the whole file for structure.
            return Unknown;
        }

        using var timeout = new CancellationTokenSource(o.ProbeTimeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            using var document = PdfDocument.Open(pdf, PdfReading.StrictParsing);
            var pages = SamplePages(document.NumberOfPages, o.ProbePages);
            if (pages.Count == 0)
            {
                return Unknown;
            }

            int withText = 0, scannedLike = 0;
            long chars = 0, garbage = 0;
            foreach (var number in pages)
            {
                linked.Token.ThrowIfCancellationRequested();
                var page = document.GetPage(number);
                var counts = PdfReading.CountCharacters(page.Letters.Select(l => l.Value));
                if (counts.Characters >= o.MinCharsPerTextPage)
                {
                    withText++;
                    chars += counts.Characters;
                    garbage += counts.Garbage;
                }
                else if (PdfReading.ImageCoverage(page) >= o.ScannedImageCoverage)
                {
                    scannedLike++;
                }
            }

            return Classify(o, pages.Count, withText, scannedLike, chars, garbage);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Only the probe's own timeout cancelled it: no verdict, the upload goes on.
            return Unknown;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The probe is advisory (spec 010): a file it cannot open (encrypted, corrupt, unusual) is accepted and the
            // ingestion run reports the precise failure code, so any parser failure means "inconclusive" here.
            return Unknown;
        }
    }

    /// <summary>Every real PDF ends with "startxref", an offset and "%%EOF"; readers tolerate a little trailing junk, so look at the last 4 KiB.</summary>
    private static bool HasCrossReferenceTrailer(Stream pdf)
    {
        if (!pdf.CanSeek)
        {
            return true;
        }

        var window = (int)Math.Min(pdf.Length, TrailerWindow);
        var tail = new byte[window];
        pdf.Seek(-window, SeekOrigin.End);
        pdf.ReadExactly(tail);
        pdf.Seek(0, SeekOrigin.Begin);
        return tail.AsSpan().IndexOf("startxref"u8) >= 0;
    }

    private static PdfProbeResult Classify(PdfOptions o, int sampled, int withText, int scannedLike, long chars, long garbage)
    {
        if ((double)withText / sampled < o.MinTextPageRatio)
        {
            return new PdfProbeResult(PdfKind.ScannedNeedsOcr, sampled, withText, scannedLike);
        }

        if (chars > 0 && (double)garbage / chars > o.MaxGarbageRatio)
        {
            return new PdfProbeResult(PdfKind.UnusableText, sampled, withText, scannedLike);
        }

        return new PdfProbeResult(withText < sampled ? PdfKind.Mixed : PdfKind.TextLayer, sampled, withText, scannedLike);
    }

    /// <summary>The first <paramref name="probePages"/> pages plus up to as many again spread evenly over the rest.</summary>
    internal static List<int> SamplePages(int pageCount, int probePages)
    {
        var pages = new List<int>();
        var head = Math.Min(pageCount, probePages);
        for (var i = 1; i <= head; i++)
        {
            pages.Add(i);
        }

        var rest = pageCount - head;
        var take = Math.Min(rest, probePages);
        for (var i = 0; i < take; i++)
        {
            pages.Add(head + 1 + (int)((long)i * rest / take));
        }

        return pages;
    }
}
