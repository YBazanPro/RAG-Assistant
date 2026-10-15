using System.Text;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Outline;
using UglyToad.PdfPig.Outline.Destinations;
using UglyToad.PdfPig.Writer;

namespace RagAssistant.Modules.Documents.Tests;

/// <summary>Builds PDFs for extractor and probe tests with PdfPig's writer: positioned text in two fonts, outlines and image-only pages.</summary>
internal static class TestPdf
{
    public const double PageWidth = 595;
    public const double PageHeight = 842;

    // A 1x1 PNG: enough to make a page "image only".
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    public sealed record Item(string Text, double X, double Y, double Size = 11, bool Bold = false);

    /// <summary>One page: a list of positioned text lines, or a full-page image when <see cref="FullPageImage"/> is set.</summary>
    public sealed record PageSpec(IReadOnlyList<Item> Items, bool FullPageImage = false);

    public static PageSpec Page(IReadOnlyList<Item> items) => new(items);

    public static PageSpec ScannedPage() => new([], FullPageImage: true);

    public static PageSpec BlankPage() => new([]);

    /// <summary>Lines of body text going down the page at a fixed pitch, starting at <paramref name="startY"/>.</summary>
    public static Item[] Body(double startY, params string[] lines) => Body(50, startY, 11, 14, lines);

    public static Item[] Body(double x, double startY, double size, double pitch, params string[] lines) =>
        [.. lines.Select((text, i) => new Item(text, x, startY - (i * pitch), size))];

    public static byte[] Create(IEnumerable<PageSpec> pages, Action<PdfDocumentBuilder>? configure = null)
    {
        var builder = new PdfDocumentBuilder();
        var regular = builder.AddStandard14Font(Standard14Font.Helvetica);
        var bold = builder.AddStandard14Font(Standard14Font.HelveticaBold);
        foreach (var spec in pages)
        {
            var page = builder.AddPage(PageSize.A4);
            if (spec.FullPageImage)
            {
                page.AddPng(TinyPng, new PdfRectangle(0, 0, PageWidth, PageHeight));
            }

            foreach (var item in spec.Items)
            {
                page.AddText(item.Text, item.Size, new PdfPoint(item.X, item.Y), item.Bold ? bold : regular);
            }
        }

        configure?.Invoke(builder);
        return builder.Build();
    }

    public static byte[] Create(params PageSpec[] pages) => Create(pages, null);

    public static Bookmarks Outline(params BookmarkNode[] roots) => new(roots);

    public static DocumentBookmarkNode Entry(string title, int level, int page, params BookmarkNode[] children) =>
        new(
            title,
            level,
            new ExplicitDestination(page, ExplicitDestinationType.XyzCoordinates, ExplicitDestinationCoordinates.Empty),
            children);

    /// <summary>A PDF that declares standard encryption with keys the empty password cannot unlock.</summary>
    public static byte[] Encrypted()
    {
        var owner = new string('A', 32);
        var user = new string('B', 32);
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] >>",
            $"<< /Filter /Standard /V 1 /R 2 /O ({owner}) /U ({user}) /P -4 >>",
        };

        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(System.Globalization.CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = pdf.Length;
        pdf.Append(System.Globalization.CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append(System.Globalization.CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        pdf.Append(System.Globalization.CultureInfo.InvariantCulture,
            $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R /Encrypt 4 0 R /ID [<00112233445566778899AABBCCDDEEFF> <00112233445566778899AABBCCDDEEFF>] >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }

    public static byte[] Corrupt() => "%PDF-1.7\nthis is not a pdf body at all\n"u8.ToArray();
}
