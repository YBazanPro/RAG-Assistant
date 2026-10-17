using System.Runtime.CompilerServices;
using System.Text;
using RagAssistant.Modules.Documents.Ingestion.Extraction;
using RagAssistant.Modules.Documents.Ingestion;
using RagAssistant.Modules.Documents.Options;
using RagAssistant.Modules.Documents.Pdf;
using static RagAssistant.Modules.Documents.Tests.TestPdf;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace RagAssistant.Modules.Documents.Tests;

/// <summary>
/// Characterization tests: every passage of a few representative inputs, compared with a file under Golden/.
/// They pin the chunking output so a refactoring of extraction and chunking can prove it changed nothing.
/// After an intended change in chunking, set RAGASSISTANT_UPDATE_GOLDEN=1, run once, and review the diff of the files.
/// </summary>
internal sealed class GoldenChunkingTests
{
    private static readonly ChunkingOptions Small = new() { MaxTokens = 60, OverlapTokens = 8, MinTokens = 20 };

    private static readonly ChunkingOptions Default = new();

    private static readonly string Markdown = string.Join('\n',
        "Intro before any heading.",
        "",
        "# Guide",
        "Short guide intro.",
        "",
        "## Setup",
        string.Join("\n\n", Enumerable.Range(1, 9).Select(n => $"Setup paragraph {n} explains one more step of the installation in plain words.")),
        "## Setup",
        "A second section with the same path.",
        "",
        "## Empty",
        "",
        "### Child",
        "Child body under a heading without body.",
        "",
        "```bash",
        "# not a heading",
        "echo first",
        "",
        "echo second",
        "```",
        "",
        "```",
        string.Join('\n', Enumerable.Range(1, 40).Select(n => $"line {n} of a long code block")),
        "```",
        "",
        "| Name | Value |",
        "|------|-------|",
        string.Join('\n', Enumerable.Range(1, 40).Select(n => $"| row {n} | value {n} |")),
        "",
        string.Join(' ', Enumerable.Range(1, 150).Select(n => $"word{n}")),
        new string('x', 300),
        "",
        "# Appendix",
        "short a",
        "## Notes",
        "short b",
        "## More",
        "short c",
        "#NoSpace is a paragraph",
        "Setext title",
        "============",
        "# Closing #",
        "Last words.");

    private static readonly string PlainText = string.Join('\n',
        "# not a heading in plain text",
        "first paragraph line one",
        "first paragraph line two",
        "",
        "",
        string.Join("\n\n", Enumerable.Range(1, 12).Select(n => $"Plain paragraph {n} carries a sentence of ordinary prose for packing.")),
        "",
        "| not | a table |",
        "```",
        "not a fence",
        string.Join(' ', Enumerable.Range(1, 120).Select(n => $"token{n}")),
        "tail.");

    private static readonly PdfBlock[] PdfBlocks =
    [
        new("Front matter before any heading.", [], 1),
        new("Chapter one opens on the first page.", ["One"], 1),
        new(Words("alpha", 60), ["One"], 1),
        new("   ", ["One"], 2),
        new("A paragraph\nwith a kept line break.", ["One"], 2),
        new(Words("beta", 200), ["One", "Detail"], 3),
        new("Back in chapter one after the detail.", ["One"], 4),
        new("Tiny.", ["Two"], 5),
        new("Also tiny.", ["Two"], 6),
        new(Words("gamma", 30), ["One"], 7),
    ];

    private static string Words(string label, int count) => string.Join(' ', Enumerable.Range(0, count).Select(i => $"{label}{i}"));

    [Test]
    [Arguments("markdown-small", false)]
    [Arguments("markdown-default", true)]
    public async Task Chunk_markdown_matchesGolden(string name, bool defaults)
    {
        var passages = Chunker.Chunk(MarkdownSourceExtractor.Parse(Markdown), defaults ? Default : Small);

        await AssertGoldenAsync(name, passages);
    }

    [Test]
    public async Task Chunk_plainText_matchesGolden()
    {
        var passages = Chunker.Chunk(PlainTextSourceExtractor.Parse(PlainText), Small);

        await AssertGoldenAsync("plaintext-small", passages);
    }

    [Test]
    public async Task Chunk_pdfBlocks_matchesGolden()
    {
        var passages = Chunker.Chunk(PdfSourceExtractor.ToDocument(PdfBlocks), Small);

        await AssertGoldenAsync("pdf-blocks-small", passages);
    }

    [Test]
    public async Task Chunk_extractedPdf_matchesGolden()
    {
        var pdf = Create(
            Page(Body(700, "The quarterly budget review covers travel expenses.", "Approvals above the limit need a second signature.")),
            Page(Body(700, "Equipment purchases follow the procurement policy.", "Every purchase above the threshold needs three quotes.")),
            Page(Body(700, Enumerable.Range(1, 30).Select(n => $"Line {n} of a long page about expense categories and limits.").ToArray())));
        var extraction = new PdfTextExtractor(MsOptions.Create(new PdfOptions())).Extract(new MemoryStream(pdf), onPageDone: null, CancellationToken.None);

        var passages = Chunker.Chunk(PdfSourceExtractor.ToDocument(extraction.Blocks), Small);

        await AssertGoldenAsync("pdf-extracted-small", passages);
    }

    private static async Task AssertGoldenAsync(string name, IReadOnlyList<ChunkedPassage> passages, [CallerFilePath] string sourceFile = "")
    {
        var actual = Serialize(passages);
        var path = Path.Combine(Path.GetDirectoryName(sourceFile)!, "Golden", $"{name}.txt");
        if (Environment.GetEnvironmentVariable("RAGASSISTANT_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, actual);
        }

        await Assert.That(File.Exists(path)).IsTrue();
        // Git may check the file out with CRLF on Windows; passages themselves never contain '\r'.
        var expected = (await File.ReadAllTextAsync(path)).ReplaceLineEndings("\n");
        await Assert.That(actual).IsEqualTo(expected);
    }

    private static string Serialize(IReadOnlyList<ChunkedPassage> passages)
    {
        var builder = new StringBuilder();
        foreach (var p in passages)
        {
            builder.Append('#').Append(p.Position)
                .Append(" path=").Append(string.Join('/', p.SectionPath))
                .Append(" ordinal=").Append(p.SectionOrdinal)
                .Append(" pages=").Append(p.PageStart?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-")
                .Append('-').Append(p.PageEnd?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-")
                .Append(" chars=").Append(p.CharCount)
                .Append(" tokens=").Append(p.TokenEstimate)
                .Append('\n').Append(p.Text).Append("\n<<<\n");
        }

        return builder.ToString();
    }
}
