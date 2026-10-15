using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace RagAssistant.Modules.Documents.Pdf;

/// <summary>What the probe and the extractor both need from a page: tolerant parsing, character counts, image coverage.</summary>
internal static class PdfReading
{
    /// <summary>For ingestion: forgiving, so slightly damaged real-world files still read.</summary>
    public static readonly ParsingOptions Parsing = new() { UseLenientParsing = true, SkipMissingFonts = true };

    /// <summary>
    /// For the upload probe: strict. Lenient mode rebuilds a missing cross-reference table by scanning the whole file, which on a
    /// 100 MiB file that merely starts with "%PDF-" takes minutes and cannot be cancelled; a strict parse fails at once instead.
    /// </summary>
    public static readonly ParsingOptions StrictParsing = new() { UseLenientParsing = false, SkipMissingFonts = true };

    public readonly record struct CharacterCounts(long Characters, long Garbage);

    /// <summary>Non-whitespace characters, and how many of those are replacement characters or symbols rather than letters, digits or punctuation.</summary>
    public static CharacterCounts CountCharacters(IEnumerable<string> texts)
    {
        long characters = 0, garbage = 0;
        foreach (var text in texts)
        {
            foreach (var c in text)
            {
                if (char.IsWhiteSpace(c))
                {
                    continue;
                }

                characters++;
                if (c == '�' || (!char.IsLetterOrDigit(c) && !char.IsPunctuation(c)))
                {
                    garbage++;
                }
            }
        }

        return new CharacterCounts(characters, garbage);
    }

    /// <summary>Share of the page area covered by images (overlaps counted twice, the sum capped at 1).</summary>
    public static double ImageCoverage(Page page)
    {
        var area = page.Width * page.Height;
        if (area <= 0)
        {
            return 0;
        }

        var covered = page.GetImages().Sum(image => Math.Min(image.BoundingBox.Area, area));
        return Math.Min(covered / area, 1);
    }
}
