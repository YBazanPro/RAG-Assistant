using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace RagAssistant.Evaluation.Corpus;

/// <summary>
/// Turns a small Markdown source (<c># title</c>, <c>## heading</c>, paragraphs) into an A4 PDF with a real text layer, using PdfPig's
/// writer (spec 018: PDFs are generated, never committed). The output is deterministic, so seeding twice uploads nothing new.
/// </summary>
internal static partial class PdfRenderer
{
    private const double PageHeight = 842;
    private const double Margin = 56;
    private const double BodySize = 10.5;
    private const double Leading = 15;

    // Helvetica averages about half an em per character; a line is cut by words below this many characters.
    private const int MaxLineChars = 92;

    public static byte[] Render(string markdown)
    {
        var builder = new PdfDocumentBuilder();
        var regular = builder.AddStandard14Font(Standard14Font.Helvetica);
        var bold = builder.AddStandard14Font(Standard14Font.HelveticaBold);
        var page = builder.AddPage(PageSize.A4);
        var y = PageHeight - Margin;

        void Line(string text, double size, PdfDocumentBuilder.AddedFont font, double after)
        {
            if (y - size < Margin)
            {
                page = builder.AddPage(PageSize.A4);
                y = PageHeight - Margin;
            }

            page.AddText(text, size, new PdfPoint(Margin, y - size), font);
            y -= size + after;
        }

        foreach (var block in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (block.StartsWith("# ", StringComparison.Ordinal))
            {
                Line(block[2..], 18, bold, 14);
            }
            else if (block.StartsWith("## ", StringComparison.Ordinal))
            {
                y -= 6;
                Line(block[3..], 13, bold, 8);
            }
            else
            {
                foreach (var line in Wrap(block.Replace('\n', ' ')))
                {
                    Line(line, BodySize, regular, Leading - BodySize);
                }

                y -= 8;
            }
        }

        return WithStableId(builder.Build(), markdown);
    }

    /// <summary>
    /// PdfPig writes a random file identifier into the trailer; it is replaced by one derived from the source, so the same source
    /// always gives the same bytes and the API's duplicate guard recognises an unchanged file. Same length, so no offset moves.
    /// </summary>
    private static byte[] WithStableId(byte[] pdf, string source)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..32];
        var stable = TrailerId().Replace(text, $"/ID [ <{id}><{id}>]", 1);
        return stable == text ? throw new InvalidOperationException("The PDF has no file identifier to replace.") : Encoding.Latin1.GetBytes(stable);
    }

    [GeneratedRegex(@"/ID \[ <[0-9A-F]{32}><[0-9A-F]{32}>\]")]
    private static partial Regex TrailerId();

    private static IEnumerable<string> Wrap(string paragraph)
    {
        var line = new List<string>();
        var length = 0;
        foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (length > 0 && length + 1 + word.Length > MaxLineChars)
            {
                yield return string.Join(' ', line);
                line.Clear();
                length = 0;
            }

            line.Add(word);
            length += (length > 0 ? 1 : 0) + word.Length;
        }

        if (line.Count > 0)
        {
            yield return string.Join(' ', line);
        }
    }
}
