using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using RagAssistant.Modules.Documents.Ingestion;
using RagAssistant.Modules.Documents.Options;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.Exceptions;
using UglyToad.PdfPig.Outline;

namespace RagAssistant.Modules.Documents.Pdf;

/// <param name="PagesWithoutText">1-based page numbers with too little text to count as text pages.</param>
/// <param name="MarginLinesRemoved">Repeated headers, footers and page numbers that were dropped.</param>
internal sealed record PdfExtraction(IReadOnlyList<SourceBlock> Blocks, int PageCount, int[] PagesWithoutText, int MarginLinesRemoved);

/// <summary>Reads the text layer of a PDF into ordered paragraphs with section paths and page numbers (spec 010). No OCR.</summary>
internal interface IPdfTextExtractor
{
    /// <exception cref="IngestionException">The PDF is encrypted, unreadable, too long, scanned or has an unusable text layer.</exception>
    PdfExtraction Extract(Stream pdf, Action<int, int>? onPageDone, CancellationToken cancellationToken);
}

internal sealed partial class PdfTextExtractor(IOptions<PdfOptions> options) : IPdfTextExtractor
{
    private const int MaxOutlineLevels = 3;
    private const double MarginBand = 0.10;
    private const double ParagraphGapRatio = 1.5;
    private const double StyleChangeRatio = 0.10;
    private const int MaxBoldHeadingChars = 80;

    private sealed class Line(string text, double size, bool bold, double top, double bottom, int page, bool newBlock)
    {
        public string Text { get; } = text;

        public double Size { get; } = size;

        public bool Bold { get; } = bold;

        public double Top { get; } = top;

        public double Bottom { get; } = bottom;

        public int Page { get; } = page;

        public bool NewBlock { get; } = newBlock;

        public bool StartsParagraph { get; set; }
    }

    private readonly record struct PageGeometry(double Bottom, double Height);

    private readonly record struct OutlineEntry(int Level, string Title, int Page);

    public PdfExtraction Extract(Stream pdf, Action<int, int>? onPageDone, CancellationToken cancellationToken)
    {
        var o = options.Value;
        PdfDocument document;
        try
        {
            document = PdfDocument.Open(pdf, PdfReading.Parsing);
        }
        catch (PdfDocumentEncryptedException)
        {
            throw new IngestionException(IngestionErrorCodes.PdfEncrypted, "The PDF is password-protected.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not IngestionException)
        {
            // Spec 010 AC-15: whatever the parser throws for a damaged file is reported as a content-free failure code.
            throw new IngestionException(IngestionErrorCodes.PdfInvalid, "The PDF could not be read.");
        }

        using (document)
        {
            try
            {
                return ExtractPages(document, o, onPageDone, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not IngestionException)
            {
                // Same rule as opening: a page the parser cannot read fails the run with a content-free code.
                throw new IngestionException(IngestionErrorCodes.PdfInvalid, "The PDF could not be read.");
            }
        }
    }

    private static PdfExtraction ExtractPages(PdfDocument document, PdfOptions o, Action<int, int>? onPageDone, CancellationToken cancellationToken)
    {
        var pageCount = document.NumberOfPages;
        if (pageCount == 0)
        {
            throw new IngestionException(IngestionErrorCodes.PdfInvalid, "The PDF has no pages.");
        }

        if (pageCount > o.MaxPages)
        {
            throw new IngestionException(IngestionErrorCodes.PdfTooManyPages, $"The PDF has more than {o.MaxPages} pages.");
        }

        var lines = new List<Line>();
        var geometry = new PageGeometry[pageCount + 1];
        var withoutText = new List<int>();
        long chars = 0, garbage = 0;
        for (var number = 1; number <= pageCount; number++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = document.GetPage(number);
            geometry[number] = new PageGeometry(page.CropBox.Bounds.Bottom, page.Height);
            var words = page.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
            var counts = PdfReading.CountCharacters(words.Select(w => w.Text));
            if (counts.Characters < o.MinCharsPerTextPage)
            {
                withoutText.Add(number);
            }
            else
            {
                chars += counts.Characters;
                garbage += counts.Garbage;
                lines.AddRange(BuildPageLines(words, number, o));
            }

            onPageDone?.Invoke(number, pageCount);
        }

        var textPages = pageCount - withoutText.Count;
        if ((double)textPages / pageCount < o.MinTextPageRatio)
        {
            throw new IngestionException(IngestionErrorCodes.RequiresOcr, "The PDF has no text layer (it looks scanned). OCR is not supported yet.");
        }

        if (chars > 0 && (double)garbage / chars > o.MaxGarbageRatio)
        {
            throw new IngestionException(IngestionErrorCodes.TextLayerUnusable, "The PDF text layer is not readable text.");
        }

        var removed = o.RemoveRepeatedMargins ? RemoveRepeatedMargins(lines, geometry, pageCount) : 0;
        var outline = ReadOutline(document, pageCount);
        var blocks = BuildBlocks(lines, outline, o);
        return new PdfExtraction(blocks, pageCount, [.. withoutText], removed);
    }

    private static List<Line> BuildPageLines(List<Word> words, int pageNumber, PdfOptions o)
    {
        var lines = new List<Line>();
        if (o.LayoutAnalysis && words.Count >= 3)
        {
            try
            {
                var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
                foreach (var block in UnsupervisedReadingOrderDetector.Instance.Get(blocks))
                {
                    var first = true;
                    foreach (var textLine in block.TextLines)
                    {
                        lines.Add(ToLine(textLine.Words, pageNumber, newBlock: first));
                        first = false;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Layout analysis is best effort on odd pages (spec 010): fall back to plain grouping in content order for this page.
                lines.Clear();
            }
        }

        if (lines.Count == 0)
        {
            lines.AddRange(PlainLines(words, pageNumber));
        }

        MarkParagraphs(lines);
        return lines;
    }

    /// <summary>Groups words into lines in content order, a new line starting when a word no longer overlaps the current one vertically.</summary>
    private static IEnumerable<Line> PlainLines(List<Word> words, int pageNumber)
    {
        var current = new List<Word>();
        foreach (var word in words)
        {
            if (current.Count > 0)
            {
                var top = current.Max(w => w.BoundingBox.Top);
                var bottom = current.Min(w => w.BoundingBox.Bottom);
                var overlap = Math.Min(top, word.BoundingBox.Top) - Math.Max(bottom, word.BoundingBox.Bottom);
                if (overlap < 0.5 * Math.Min(top - bottom, word.BoundingBox.Height))
                {
                    yield return ToLine([.. current.OrderBy(w => w.BoundingBox.Left)], pageNumber, newBlock: false);
                    current.Clear();
                }
            }

            current.Add(word);
        }

        if (current.Count > 0)
        {
            yield return ToLine([.. current.OrderBy(w => w.BoundingBox.Left)], pageNumber, newBlock: false);
        }
    }

    private static Line ToLine(IReadOnlyList<Word> words, int pageNumber, bool newBlock)
    {
        var letters = words.SelectMany(w => w.Letters).ToList();
        var size = letters.Count == 0 ? 0 : letters.Average(l => l.PointSize);
        var bold = letters.Count > 0
            && letters.Count(l => l.FontDetails.IsBold || l.FontName?.Contains("bold", StringComparison.OrdinalIgnoreCase) == true) * 2 >= letters.Count;
        return new Line(
            string.Join(' ', words.Select(w => w.Text)),
            size,
            bold,
            words.Max(w => w.BoundingBox.Top),
            words.Min(w => w.BoundingBox.Bottom),
            pageNumber,
            newBlock);
    }

    /// <summary>A paragraph starts at a layout block, after a gap above 1.5x the median line pitch, or when size or weight changes.</summary>
    private static void MarkParagraphs(List<Line> lines)
    {
        var pitches = new List<double>();
        for (var i = 1; i < lines.Count; i++)
        {
            var pitch = lines[i - 1].Top - lines[i].Top;
            if (pitch > 0)
            {
                pitches.Add(pitch);
            }
        }

        var median = pitches.Count == 0 ? 0 : pitches.Order().ElementAt(pitches.Count / 2);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (i == 0 || line.NewBlock)
            {
                line.StartsParagraph = true;
                continue;
            }

            var previous = lines[i - 1];
            var pitch = previous.Top - line.Top;
            line.StartsParagraph = pitch <= 0
                || (median > 0 && pitch > ParagraphGapRatio * median)
                || line.Bold != previous.Bold
                || Math.Abs(line.Size - previous.Size) > StyleChangeRatio * previous.Size;
        }
    }

    private static int RemoveRepeatedMargins(List<Line> lines, PageGeometry[] geometry, int pageCount)
    {
        bool InBand(Line line)
        {
            var g = geometry[line.Page];
            return g.Height > 0
                && (line.Top - g.Bottom > g.Height * (1 - MarginBand) || line.Bottom - g.Bottom < g.Height * MarginBand);
        }

        var pagesByKey = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        foreach (var line in lines.Where(InBand))
        {
            var key = MarginKey(line.Text);
            if (key.Length > 0)
            {
                if (!pagesByKey.TryGetValue(key, out var pages))
                {
                    pagesByKey[key] = pages = [];
                }

                pages.Add(line.Page);
            }
        }

        // On at least half of the pages, and never on a single page: a one-page document keeps its first and last line.
        var threshold = Math.Max(2, (pageCount + 1) / 2);
        var removed = 0;
        var keep = new List<Line>(lines.Count);
        var previousRemoved = false;
        foreach (var line in lines)
        {
            var drop = InBand(line) && (PageNumberOnly().IsMatch(line.Text) || (pagesByKey.TryGetValue(MarginKey(line.Text), out var pages) && pages.Count >= threshold));
            if (drop)
            {
                removed++;
                previousRemoved = true;
                continue;
            }

            if (previousRemoved)
            {
                line.StartsParagraph = true;
                previousRemoved = false;
            }

            keep.Add(line);
        }

        lines.Clear();
        lines.AddRange(keep);
        return removed;
    }

    /// <summary>Digits are ignored so "Page 3 of 10" and "Page 4 of 10" are the same repeated line.</summary>
    private static string MarginKey(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (!char.IsDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return Whitespace().Replace(sb.ToString(), " ").Trim();
    }

    private static List<OutlineEntry> ReadOutline(PdfDocument document, int pageCount)
    {
        try
        {
            if (!document.TryGetBookmarks(out var bookmarks))
            {
                return [];
            }

            var entries = new List<OutlineEntry>();

            void Walk(IEnumerable<BookmarkNode> nodes, int depth)
            {
                foreach (var node in nodes)
                {
                    if (node is DocumentBookmarkNode { PageNumber: >= 1 } target && target.PageNumber <= pageCount && !string.IsNullOrWhiteSpace(target.Title))
                    {
                        entries.Add(new OutlineEntry(Math.Min(depth, MaxOutlineLevels), target.Title.Trim(), target.PageNumber));
                    }

                    Walk(node.Children, depth + 1);
                }
            }

            Walk(bookmarks.Roots, 1);
            return entries;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The outline is optional metadata: a broken one must not fail a PDF whose text reads fine; fonts decide the headings then.
            return [];
        }
    }

    private static List<SourceBlock> BuildBlocks(List<Line> lines, List<OutlineEntry> outline, PdfOptions o)
    {
        var headingLevel = new int[lines.Count];
        var outlineStarts = new List<(int Start, int Level, string Title)>();
        var skip = new HashSet<int>();
        if (outline.Count > 0)
        {
            foreach (var entry in outline)
            {
                var onPage = lines.FindIndex(l => l.Page >= entry.Page);
                if (onPage < 0)
                {
                    continue;
                }

                var title = Normalize(entry.Title);
                var match = lines.FindIndex(onPage, l => l.Page == entry.Page && Normalize(l.Text) == title);
                if (match >= 0)
                {
                    skip.Add(match);
                }

                outlineStarts.Add((match >= 0 ? match : onPage, entry.Level, entry.Title));
            }

            outlineStarts = [.. outlineStarts.OrderBy(s => s.Start)];
        }

        if (outlineStarts.Count == 0)
        {
            headingLevel = DetectFontHeadings(lines, o);
        }

        var blocks = new List<SourceBlock>();
        var chain = new List<(int Level, string Title)>();
        var paragraph = new StringBuilder();
        var paragraphPage = 0;
        var nextOutline = 0;

        void Flush()
        {
            if (paragraph.Length > 0)
            {
                blocks.Add(new SourceBlock(paragraph.ToString(), [.. chain.Select(h => h.Title)], paragraphPage));
                paragraph.Clear();
            }
        }

        void Enter(int level, string title)
        {
            Flush();
            chain.RemoveAll(h => h.Level >= level);
            chain.Add((level, title));
        }

        for (var i = 0; i < lines.Count; i++)
        {
            while (nextOutline < outlineStarts.Count && outlineStarts[nextOutline].Start <= i)
            {
                Enter(outlineStarts[nextOutline].Level, outlineStarts[nextOutline].Title);
                nextOutline++;
            }

            var line = lines[i];
            if (skip.Contains(i))
            {
                Flush();
                continue;
            }

            if (headingLevel[i] > 0)
            {
                Enter(headingLevel[i], line.Text.Trim());
                continue;
            }

            if (line.StartsParagraph || line.Page != paragraphPage)
            {
                Flush();
            }

            Append(paragraph, line.Text);
            paragraphPage = line.Page;
        }

        Flush();
        return blocks;
    }

    private static void Append(StringBuilder paragraph, string text)
    {
        text = text.Trim();
        if (paragraph.Length == 0)
        {
            paragraph.Append(text);
            return;
        }

        // "inter-" + "national" is one word split by the line break; "well-" + "Known" is not.
        if (paragraph[^1] == '-' && paragraph.Length >= 2 && char.IsLetter(paragraph[^2]) && text.Length > 0 && char.IsLower(text[0]))
        {
            paragraph.Length--;
            paragraph.Append(text);
            return;
        }

        paragraph.Append(' ').Append(text);
    }

    /// <summary>Without an outline: single-line paragraphs set larger than the body text, or bold, become headings; sizes rank into levels.</summary>
    private static int[] DetectFontHeadings(List<Line> lines, PdfOptions o)
    {
        var levels = new int[lines.Count];
        if (lines.Count == 0)
        {
            return levels;
        }

        var bodySize = lines
            .GroupBy(l => RoundSize(l.Size))
            .OrderByDescending(g => g.Sum(l => l.Text.Length))
            .First().Key;

        var candidates = new List<(int Index, double Size)>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var singleLine = line.StartsParagraph && (i + 1 == lines.Count || lines[i + 1].StartsParagraph || lines[i + 1].Page != line.Page);
            if (!singleLine)
            {
                continue;
            }

            var text = line.Text.Trim();
            var larger = text.Length <= o.MaxHeadingChars && line.Size >= o.HeadingMinSizeRatio * bodySize;
            var bold = line.Bold && line.Size >= bodySize && text.Length < MaxBoldHeadingChars && !text.EndsWith('.');
            if (larger || bold)
            {
                candidates.Add((i, RoundSize(line.Size)));
            }
        }

        var ranked = candidates.Select(c => c.Size).Distinct().OrderByDescending(s => s).ToList();
        foreach (var (index, size) in candidates)
        {
            levels[index] = Math.Min(ranked.IndexOf(size) + 1, o.MaxHeadingLevels);
        }

        return levels;
    }

    private static double RoundSize(double size) => Math.Round(size * 2, MidpointRounding.AwayFromZero) / 2;

    private static string Normalize(string text) => Whitespace().Replace(text, " ").Trim().ToLowerInvariant();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^\W*(page\s+|p\.\s*)?\d{1,5}(\s*(of|/)\s*\d{1,5})?\W*$", RegexOptions.IgnoreCase)]
    private static partial Regex PageNumberOnly();
}
