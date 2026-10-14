using RagAssistant.Modules.Documents.Domain;
using RagAssistant.Modules.Documents.Ingestion;
using RagAssistant.Modules.Documents.Options;

namespace RagAssistant.Modules.Documents.Tests;

internal sealed class ChunkerTests
{
    private static ChunkingOptions Options(int max = 400, int overlap = 50, int min = 1) =>
        new() { MaxTokens = max, OverlapTokens = overlap, MinTokens = min };

    private static IReadOnlyList<ChunkedPassage> Markdown(string content, ChunkingOptions? options = null) =>
        Chunker.Chunk(content, SourceFormat.Markdown, options ?? Options());

    private static string Paragraphs(int count, string prefix = "paragraph number") =>
        string.Join("\n\n", Enumerable.Range(1, count).Select(n => $"{prefix} {n} has several words"));

    [Test]
    public async Task Chunk_plainText_splitsOnParagraphsWithEmptySectionPath()
    {
        var text = "first paragraph of the file\n\nsecond paragraph of the file\n\nthird paragraph of the file";

        var passages = Chunker.Chunk(text, SourceFormat.PlainText, Options(max: 10, overlap: 1));

        await Assert.That(passages.Count).IsEqualTo(3);
        await Assert.That(passages.All(p => p.SectionPath.Length == 0)).IsTrue();
        await Assert.That(passages[0].Text).IsEqualTo("first paragraph of the file");
        await Assert.That(passages[1].Text).Contains("second paragraph of the file");
        await Assert.That(passages[2].Text).Contains("third paragraph of the file");
    }

    [Test]
    public async Task Chunk_plainTextWithHashLines_doesNotTreatThemAsHeadings()
    {
        var passages = Chunker.Chunk("# not a heading\nbody", SourceFormat.PlainText, Options());

        await Assert.That(passages.Count).IsEqualTo(1);
        await Assert.That(passages[0].SectionPath.Length).IsEqualTo(0);
        await Assert.That(passages[0].Text).IsEqualTo("# not a heading\nbody");
    }

    [Test]
    public async Task Chunk_nestedHeadings_setsPathPositionsAndOrdinals()
    {
        var md = "# A\nbody a\n## B\nbody b\n## C\nbody c\n# D\nbody d";

        var passages = Markdown(md);

        await Assert.That(passages.Count).IsEqualTo(4);
        await Assert.That(passages.Select(p => string.Join('/', p.SectionPath)).ToArray()).IsEquivalentTo(["A", "A/B", "A/C", "D"]);
        await Assert.That(passages.Select(p => p.Text).ToArray()).IsEquivalentTo(["body a", "body b", "body c", "body d"]);
        await Assert.That(passages.Select(p => p.Position).ToArray()).IsEquivalentTo([0, 1, 2, 3]);
        await Assert.That(passages.All(p => p.SectionOrdinal == 0)).IsTrue();
    }

    [Test]
    public async Task Chunk_repeatedPath_restartsOrdinalPerPath()
    {
        var md = $"# A\n{Paragraphs(8)}\n# B\nshort";

        var passages = Markdown(md, Options(max: 20, overlap: 2));

        var a = passages.Where(p => p.SectionPath is ["A"]).ToList();
        await Assert.That(a.Count).IsGreaterThan(1);
        await Assert.That(a.Select(p => p.SectionOrdinal).ToArray()).IsEquivalentTo(Enumerable.Range(0, a.Count).ToArray());
        await Assert.That(passages[^1].SectionPath).IsEquivalentTo(["B"]);
        await Assert.That(passages[^1].SectionOrdinal).IsEqualTo(0);
    }

    [Test]
    public async Task Chunk_textBeforeFirstHeading_hasEmptyPath()
    {
        var passages = Markdown("preamble text\n# A\nbody");

        await Assert.That(passages.Count).IsEqualTo(2);
        await Assert.That(passages[0].SectionPath.Length).IsEqualTo(0);
        await Assert.That(passages[0].Text).IsEqualTo("preamble text");
    }

    [Test]
    public async Task Chunk_skippedAndReplacedLevels_buildTheHeadingChain()
    {
        var md = "# A\n### C\nc body\n## D\nd body\n### E\ne body";

        var passages = Markdown(md);

        await Assert.That(passages.Select(p => string.Join('/', p.SectionPath)).ToArray()).IsEquivalentTo(["A/C", "A/D", "A/D/E"]);
    }

    [Test]
    public async Task Chunk_headingInsideFence_isNotAHeading()
    {
        var md = "# A\n```\n# not a heading\n```\nafter";

        var passages = Markdown(md);

        await Assert.That(passages.Count).IsEqualTo(1);
        await Assert.That(passages[0].SectionPath).IsEquivalentTo(["A"]);
        await Assert.That(passages[0].Text).Contains("# not a heading");
    }

    [Test]
    public async Task Chunk_hashWithoutSpaceAndSetext_arePlainParagraphs()
    {
        var passages = Markdown("#hashtag\n\nTitle\n=====\n\nbody");

        await Assert.That(passages.All(p => p.SectionPath.Length == 0)).IsTrue();
        var all = string.Join("\n", passages.Select(p => p.Text));
        await Assert.That(all).Contains("#hashtag");
        await Assert.That(all).Contains("Title\n=====");
    }

    [Test]
    public async Task Chunk_headingWithoutBody_producesNoPassageButStaysInDescendantPath()
    {
        var passages = Markdown("# A\n## B\ntext");

        await Assert.That(passages.Count).IsEqualTo(1);
        await Assert.That(passages[0].SectionPath).IsEquivalentTo(["A", "B"]);
    }

    [Test]
    public async Task Chunk_onlyHeadings_returnsNothing()
    {
        await Assert.That(Markdown("# A\n## B\n### C\n").Count).IsEqualTo(0);
    }

    [Test]
    public async Task Chunk_largeSection_staysWithinMaxAndOverlapsTheTail()
    {
        var options = Options(max: 20, overlap: 5);

        var passages = Markdown($"# A\n{Paragraphs(10)}", options);

        await Assert.That(passages.Count).IsGreaterThan(2);
        await Assert.That(passages.All(p => p.TokenEstimate <= 20)).IsTrue();
        for (var i = 1; i < passages.Count; i++)
        {
            var lastWord = passages[i - 1].Text.Split(' ', '\n')[^1];
            await Assert.That(passages[i].Text.IndexOf(lastWord, StringComparison.Ordinal)).IsBetween(0, 20);
        }
    }

    [Test]
    public async Task Chunk_twoSections_doNotOverlap()
    {
        var passages = Markdown("# A\nalpha alpha alpha alpha\n# B\nbeta beta beta beta", Options(max: 20, overlap: 5));

        await Assert.That(passages.Count).IsEqualTo(2);
        await Assert.That(passages[1].Text).IsEqualTo("beta beta beta beta");
    }

    [Test]
    public async Task Chunk_fencedCodeWithBlankLines_isNeverSplit()
    {
        var code = "```csharp\nvar a = 1;\n\nvar b = 2;\n```";
        var md = $"# A\nintro paragraph that is fairly long to force a new passage\n\n{code}";

        var passages = Markdown(md, Options(max: 20, overlap: 2));

        await Assert.That(passages.Count(p => p.Text.Contains(code, StringComparison.Ordinal))).IsEqualTo(1);
    }

    [Test]
    public async Task Chunk_table_isNeverSplit()
    {
        var table = "| a | b |\n|---|---|\n| 1 | 2 |\n| 3 | 4 |";
        var md = $"# A\nintro paragraph that is fairly long to force a new passage\n\n{table}";

        var passages = Markdown(md, Options(max: 25, overlap: 2));

        await Assert.That(passages.Count(p => p.Text.Contains(table, StringComparison.Ordinal))).IsEqualTo(1);
    }

    [Test]
    public async Task Chunk_passageFollowingACodeBlock_doesNotStartWithTheFenceTail()
    {
        var md = $"# A\n```\nx = 1;\n```\n\n{string.Join(' ', Enumerable.Repeat("word", 30))}";

        var passages = Markdown(md, Options(max: 20, overlap: 5));

        await Assert.That(passages.Count).IsGreaterThan(1);
        foreach (var passage in passages.Skip(1))
        {
            await Assert.That(passage.Text).DoesNotContain("```");
        }
    }

    [Test]
    public async Task Chunk_oversizedCodeBlock_splitsOnLinesAndRefences()
    {
        var lines = Enumerable.Range(1, 30).Select(n => $"line{n:00};").ToArray();
        var md = $"# A\n~~~js\n{string.Join('\n', lines)}\n~~~";

        var passages = Markdown(md, Options(max: 20, overlap: 2));

        await Assert.That(passages.Count).IsGreaterThan(1);
        foreach (var p in passages)
        {
            await Assert.That(p.TokenEstimate).IsLessThanOrEqualTo(20);
            await Assert.That(p.Text).StartsWith("~~~js\n");
            await Assert.That(p.Text).EndsWith("\n~~~");
        }

        var body = string.Join('\n', passages.Select(p => p.Text));
        foreach (var line in lines)
        {
            await Assert.That(body).Contains(line);
        }
    }

    [Test]
    public async Task Chunk_oversizedTable_repeatsHeaderRows()
    {
        var rows = Enumerable.Range(1, 20).Select(n => $"| row{n:00} | x |");
        var md = $"# A\n| h1 | h2 |\n|----|----|\n{string.Join('\n', rows)}";

        var passages = Markdown(md, Options(max: 25, overlap: 2));

        await Assert.That(passages.Count).IsGreaterThan(1);
        foreach (var p in passages)
        {
            await Assert.That(p.TokenEstimate).IsLessThanOrEqualTo(25);
            await Assert.That(p.Text).StartsWith("| h1 | h2 |\n|----|----|\n");
        }
    }

    [Test]
    public async Task Chunk_singleLongLine_splitsAtWhitespaceWithinMax()
    {
        var line = string.Join(' ', Enumerable.Repeat("word", 60));

        var passages = Chunker.Chunk(line, SourceFormat.PlainText, Options(max: 20, overlap: 2));

        await Assert.That(passages.Count).IsGreaterThan(2);
        await Assert.That(passages.All(p => p.TokenEstimate <= 20)).IsTrue();
        await Assert.That(passages.All(p => !p.Text.StartsWith(' ') && !p.Text.EndsWith(' '))).IsTrue();
    }

    [Test]
    public async Task Chunk_lineWithoutWhitespace_splitsAtTheLimit()
    {
        var passages = Chunker.Chunk(new string('x', 200), SourceFormat.PlainText, Options(max: 10, overlap: 1));

        await Assert.That(passages.Count).IsGreaterThan(4);
        await Assert.That(passages.All(p => p.TokenEstimate <= 10)).IsTrue();
        await Assert.That(passages.Sum(p => p.Text.Count(c => c == 'x'))).IsGreaterThanOrEqualTo(200);
    }

    [Test]
    public async Task Chunk_smallSiblingSections_mergeIntoTheNextWithHeadingPrefix()
    {
        var passages = Markdown("# A\nshort a\n# B\nshort b", Options(min: 80));

        await Assert.That(passages.Count).IsEqualTo(1);
        await Assert.That(passages[0].SectionPath).IsEquivalentTo(["A"]);
        await Assert.That(passages[0].Text).IsEqualTo("short a\n\n# B\nshort b");
    }

    [Test]
    public async Task Chunk_smallPassageWithDifferentParent_isKeptAsIs()
    {
        var passages = Markdown("# P\n## A1\nshort a\n# Q\nshort q", Options(min: 80));

        await Assert.That(passages.Count).IsEqualTo(2);
        await Assert.That(passages[0].Text).IsEqualTo("short a");
        await Assert.That(passages[1].Text).IsEqualTo("short q");
    }

    [Test]
    public async Task Chunk_mergeThatWouldExceedMax_isSkipped()
    {
        var big = new string('a', 70) + " b";
        var passages = Markdown($"# A\n{big}\n# B\ntiny", Options(max: 20, overlap: 2, min: 30));

        await Assert.That(passages.Count).IsEqualTo(2);
        await Assert.That(passages.All(p => p.TokenEstimate <= 20)).IsTrue();
    }

    [Test]
    public async Task Chunk_anyInput_keepsEveryBodyLine()
    {
        var md = "intro\n# A\nalpha one\n\nalpha two\n## B\nbeta\n# C\ngamma\n\n```\ncode line\n```\n| t |\n|---|\n| 1 |";

        var passages = Markdown(md, Options(max: 12, overlap: 2, min: 20));

        var all = string.Join("\n", passages.Select(p => p.Text));
        foreach (var line in new[] { "intro", "alpha one", "alpha two", "beta", "gamma", "code line", "| 1 |" })
        {
            await Assert.That(all).Contains(line);
        }
    }

    [Test]
    public async Task Chunk_passage_hasCharCountAndTokenEstimate()
    {
        var passages = Chunker.Chunk("abcde", SourceFormat.PlainText, Options());

        await Assert.That(passages[0].CharCount).IsEqualTo(5);
        await Assert.That(passages[0].TokenEstimate).IsEqualTo(2);
    }
}
