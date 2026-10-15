using System.Text;
using System.Text.RegularExpressions;
using RagAssistant.Modules.Documents.Domain;
using RagAssistant.Modules.Documents.Options;

namespace RagAssistant.Modules.Documents.Ingestion;

internal sealed record ChunkedPassage(int Position, string[] SectionPath, int SectionOrdinal, string Text, int? PageStart = null, int? PageEnd = null)
{
    public int CharCount => Text.Length;

    public int TokenEstimate => Chunker.EstimateTokens(Text.Length);
}

/// <summary>
/// Heading-aware chunking of Markdown and plain text (spec 007). Pure: string in, passages out.
/// Sizes are measured in characters internally; a passage of at most <c>MaxTokens</c> estimated tokens has at most <c>4 * MaxTokens</c> characters.
/// </summary>
internal static partial class Chunker
{
    private const int CharsPerToken = 4;
    private const string BlockSeparator = "\n\n";

    public static int EstimateTokens(int chars) => (chars + CharsPerToken - 1) / CharsPerToken;

    public static IReadOnlyList<ChunkedPassage> Chunk(string content, SourceFormat format, ChunkingOptions options)
    {
        var markdown = format == SourceFormat.Markdown;
        var sections = markdown ? ParseMarkdown(content) : [new Section([], null, ParseBlocks(content.Split('\n'), markdown: false))];
        return Assemble(sections, options);
    }

    /// <summary>Chunks the paragraphs of an extracted PDF (spec 010); every passage records the first and last page of its text.</summary>
    public static IReadOnlyList<ChunkedPassage> Chunk(IReadOnlyList<SourceBlock> blocks, ChunkingOptions options)
    {
        var sections = new List<Section>();
        foreach (var block in blocks.Where(b => !string.IsNullOrWhiteSpace(b.Text)))
        {
            if (sections.Count == 0 || !sections[^1].Path.AsSpan().SequenceEqual(block.SectionPath))
            {
                sections.Add(new Section(block.SectionPath, null, []));
            }

            sections[^1].Blocks.Add(new Block(BlockKind.Paragraph, [.. block.Text.Split('\n')], block.Page));
        }

        return Assemble(sections, options);
    }

    private static List<ChunkedPassage> Assemble(List<Section> sections, ChunkingOptions options)
    {
        var limits = new Limits(options.MaxTokens * CharsPerToken, options.OverlapTokens * CharsPerToken);

        var drafts = new List<Draft>();
        for (var i = 0; i < sections.Count; i++)
        {
            Pack(sections[i], i, limits, drafts);
        }

        MergeSmall(drafts, options.MinTokens, limits);
        ApplyOverlap(drafts, limits);
        return Finish(drafts);
    }

    private readonly record struct Limits(int Max, int Overlap)
    {
        /// <summary>Room kept free in every passage that may receive an overlap prefix (prefix plus one newline).</summary>
        public int Reserve => Overlap + 1;

        public int Continuation => Math.Max(Max - Reserve, 1);
    }

    private enum BlockKind
    {
        Paragraph,
        Code,
        Table,
    }

    private sealed record Block(BlockKind Kind, List<string> Lines, int Page = 0)
    {
        public string Text => string.Join('\n', Lines);
    }

    private sealed record Section(string[] Path, string? HeadingLine, List<Block> Blocks);

    private sealed class Draft(string[] path, string text, int section, string? heading, bool noOverlap)
    {
        public string[] Path { get; } = path;

        public string Text { get; set; } = text;

        public int StartSection { get; init; } = section;

        public int EndSection { get; set; } = section;

        public string? StartHeading { get; init; } = heading;

        /// <summary>Set for pieces of a split code block or table: they are self-contained (fence, header rows) and get no prefix.</summary>
        public bool NoOverlap { get; init; } = noOverlap;

        /// <summary>First and last page of the text, 0 when the source has no pages.</summary>
        public int PageStart { get; set; }

        public int PageEnd { get; set; }
    }

    private static int FirstPage(int a, int b) => a == 0 ? b : b == 0 ? a : Math.Min(a, b);

    private static List<Section> ParseMarkdown(string content)
    {
        var sections = new List<Section>();
        var chain = new List<(int Level, string Title)>();
        var path = Array.Empty<string>();
        string? heading = null;
        var body = new List<string>();
        (char Char, int Length)? fence = null;

        void Close() => sections.Add(new Section(path, heading, ParseBlocks(body, markdown: true)));

        foreach (var line in content.Split('\n'))
        {
            if (fence is { } open)
            {
                body.Add(line);
                if (ClosesFence(line, open))
                {
                    fence = null;
                }

                continue;
            }

            if (TryOpenFence(line, out var opened))
            {
                fence = opened;
                body.Add(line);
                continue;
            }

            var match = AtxHeading().Match(line);
            if (!match.Success)
            {
                body.Add(line);
                continue;
            }

            Close();
            var level = match.Groups[1].Length;
            chain.RemoveAll(h => h.Level >= level);
            chain.Add((level, match.Groups[2].Value.Trim()));
            path = [.. chain.Select(h => h.Title)];
            heading = line.TrimEnd();
            body = [];
        }

        Close();
        return sections;
    }

    private static List<Block> ParseBlocks(IReadOnlyList<string> lines, bool markdown)
    {
        var blocks = new List<Block>();
        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count > 0)
            {
                blocks.Add(new Block(BlockKind.Paragraph, paragraph));
                paragraph = [];
            }
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                continue;
            }

            if (markdown && TryOpenFence(line, out var fence))
            {
                FlushParagraph();
                var code = new List<string> { line };
                while (++i < lines.Count)
                {
                    code.Add(lines[i]);
                    if (ClosesFence(lines[i], fence))
                    {
                        break;
                    }
                }

                blocks.Add(new Block(BlockKind.Code, code));
                continue;
            }

            if (markdown && IsTableLine(line))
            {
                FlushParagraph();
                var table = new List<string> { line };
                while (i + 1 < lines.Count && IsTableLine(lines[i + 1]))
                {
                    table.Add(lines[++i]);
                }

                blocks.Add(new Block(BlockKind.Table, table));
                continue;
            }

            paragraph.Add(line);
        }

        FlushParagraph();
        return blocks;
    }

    private static bool IsTableLine(string line) => line.TrimStart().StartsWith('|');

    private static bool TryOpenFence(string line, out (char Char, int Length) fence)
    {
        var match = FenceOpen().Match(line);
        fence = match.Success ? (match.Groups[1].Value[0], match.Groups[1].Length) : default;
        return match.Success;
    }

    private static bool ClosesFence(string line, (char Char, int Length) fence)
    {
        var trimmed = line.Trim();
        return trimmed.Length >= fence.Length && trimmed.All(c => c == fence.Char);
    }

    private static void Pack(Section section, int index, Limits limits, List<Draft> drafts)
    {
        var count = 0;
        var current = new StringBuilder();
        var currentStart = 0;
        var currentEnd = 0;

        int Budget() => count == 0 ? limits.Max : limits.Continuation;

        void Emit(string text, bool noOverlap, int pageStart, int pageEnd)
        {
            drafts.Add(new Draft(section.Path, text, index, section.HeadingLine, noOverlap) { PageStart = pageStart, PageEnd = pageEnd });
            count++;
        }

        void Take(Block block)
        {
            currentStart = FirstPage(currentStart, block.Page);
            currentEnd = Math.Max(currentEnd, block.Page);
        }

        void Flush()
        {
            if (current.Length > 0)
            {
                Emit(current.ToString(), noOverlap: false, currentStart, currentEnd);
                current.Clear();
                currentStart = 0;
                currentEnd = 0;
            }
        }

        foreach (var block in section.Blocks)
        {
            var text = block.Text;
            if (current.Length > 0)
            {
                if (current.Length + BlockSeparator.Length + text.Length <= Budget())
                {
                    current.Append(BlockSeparator).Append(text);
                    Take(block);
                    continue;
                }

                Flush();
            }

            if (text.Length <= Budget())
            {
                current.Append(text);
                Take(block);
                continue;
            }

            var first = Budget();
            var pieces = Split(block, i => i == 0 ? first : limits.Continuation);
            foreach (var piece in pieces)
            {
                Emit(piece, noOverlap: block.Kind != BlockKind.Paragraph, block.Page, block.Page);
            }
        }

        Flush();
    }

    private static List<string> Split(Block block, Func<int, int> budgetFor)
    {
        switch (block.Kind)
        {
            case BlockKind.Code when block.Lines.Count >= 2:
                {
                    var open = block.Lines[0];
                    TryOpenFence(open, out var fence);
                    var closed = ClosesFence(block.Lines[^1], fence);
                    var close = closed ? block.Lines[^1] : new string(fence.Char, fence.Length);
                    var inner = block.Lines.Skip(1).Take(block.Lines.Count - (closed ? 2 : 1)).ToList();
                    var overhead = open.Length + close.Length + 2;
                    if (budgetFor(0) - overhead >= 1 && budgetFor(1) - overhead >= 1)
                    {
                        return [.. SplitLines(inner, i => budgetFor(i) - overhead).Select(p => $"{open}\n{p}\n{close}")];
                    }

                    break;
                }

            case BlockKind.Table:
                {
                    var headerCount = block.Lines.Count > 1 && TableSeparator().IsMatch(block.Lines[1]) ? 2 : 1;
                    var header = string.Join('\n', block.Lines.Take(headerCount));
                    var rows = block.Lines.Skip(headerCount).ToList();
                    var overhead = header.Length + 1;
                    if (rows.Count > 0 && budgetFor(0) - overhead >= 1 && budgetFor(1) - overhead >= 1)
                    {
                        return [.. SplitLines(rows, i => budgetFor(i) - overhead).Select(p => $"{header}\n{p}")];
                    }

                    break;
                }
        }

        return SplitLines(block.Lines, budgetFor);
    }

    /// <summary>Packs whole lines into pieces; a line over the budget is split at whitespace, or at the limit when it has none.</summary>
    private static List<string> SplitLines(IReadOnlyList<string> lines, Func<int, int> budgetFor)
    {
        var pieces = new List<string>();
        var current = new StringBuilder();
        var linesInCurrent = 0;

        int Budget() => Math.Max(budgetFor(pieces.Count), 1);

        void Flush()
        {
            if (linesInCurrent > 0)
            {
                pieces.Add(current.ToString());
                current.Clear();
                linesInCurrent = 0;
            }
        }

        foreach (var line in lines)
        {
            if (linesInCurrent > 0 && current.Length + 1 + line.Length > Budget())
            {
                Flush();
            }

            var remaining = line;
            while (remaining.Length > Budget())
            {
                var budget = Budget();
                var cut = remaining.AsSpan(0, budget + 1).LastIndexOfAny(" \t");
                var piece = cut > 0 ? remaining[..cut] : remaining[..budget];
                remaining = (cut > 0 ? remaining[(cut + 1)..] : remaining[budget..]).TrimStart();
                if (piece.Length > 0)
                {
                    pieces.Add(piece);
                }
            }

            if (remaining.Length == 0 && line.Length > 0)
            {
                continue;
            }

            if (linesInCurrent > 0)
            {
                current.Append('\n');
            }

            current.Append(remaining);
            linesInCurrent++;
        }

        Flush();
        return pieces;
    }

    private static bool CanReceiveOverlap(List<Draft> drafts, int index) =>
        index > 0 && !drafts[index].NoOverlap && drafts[index - 1].EndSection == drafts[index].StartSection;

    private static void MergeSmall(List<Draft> drafts, int minTokens, Limits limits)
    {
        var i = 0;
        while (i < drafts.Count)
        {
            var draft = drafts[i];
            if (EstimateTokens(draft.Text.Length) >= minTokens)
            {
                i++;
                continue;
            }

            if (i > 0 && drafts[i - 1].Path.AsSpan().SequenceEqual(draft.Path) && TryMerge(drafts, i - 1, limits))
            {
                continue;
            }

            if (i + 1 < drafts.Count && SameParent(draft.Path, drafts[i + 1].Path) && TryMerge(drafts, i, limits))
            {
                continue;
            }

            i++;
        }
    }

    private static bool SameParent(string[] a, string[] b) =>
        a.Length > 0 && b.Length > 0 && a.AsSpan(0, a.Length - 1).SequenceEqual(b.AsSpan(0, b.Length - 1));

    /// <summary>Merges drafts[at + 1] into drafts[at] when the result fits; the later part keeps its heading line when it starts a section.</summary>
    private static bool TryMerge(List<Draft> drafts, int at, Limits limits)
    {
        var first = drafts[at];
        var second = drafts[at + 1];
        var part = second.StartSection != first.EndSection && second.StartHeading is { } heading
            ? $"{heading}\n{second.Text}"
            : second.Text;
        var text = first.Text + BlockSeparator + part;
        var limit = CanReceiveOverlap(drafts, at) ? limits.Continuation : limits.Max;
        if (text.Length > limit)
        {
            return false;
        }

        first.Text = text;
        first.PageStart = FirstPage(first.PageStart, second.PageStart);
        first.PageEnd = Math.Max(first.PageEnd, second.PageEnd);
        first.EndSection = second.EndSection;
        drafts.RemoveAt(at + 1);
        return true;
    }

    private static void ApplyOverlap(List<Draft> drafts, Limits limits)
    {
        if (limits.Overlap <= 0)
        {
            return;
        }

        // Back to front, so the previous draft still holds its own text without a prefix.
        for (var i = drafts.Count - 1; i > 0; i--)
        {
            if (!CanReceiveOverlap(drafts, i))
            {
                continue;
            }

            var tail = Tail(drafts[i - 1].Text, limits.Overlap);
            // A tail that cuts into a code fence or a table would start the passage with unbalanced syntax; no overlap is better.
            if (tail.Length > 0 && !StructuralLine().IsMatch(tail))
            {
                drafts[i].Text = $"{tail}\n{drafts[i].Text}";
                // The prefix is the end of the previous passage, so its page counts too (approximated by that passage's last page).
                drafts[i].PageStart = FirstPage(drafts[i].PageStart, drafts[i - 1].PageEnd);
            }
        }
    }

    /// <summary>The last <paramref name="chars"/> characters, moved forward to the next word start.</summary>
    private static string Tail(string text, int chars)
    {
        if (text.Length <= chars)
        {
            return text;
        }

        var start = text.Length - chars;
        if (!char.IsWhiteSpace(text[start - 1]) && !char.IsWhiteSpace(text[start]))
        {
            while (start < text.Length && !char.IsWhiteSpace(text[start]))
            {
                start++;
            }

            if (start == text.Length)
            {
                return string.Empty;
            }
        }

        while (start < text.Length && char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        return text[start..];
    }

    private static List<ChunkedPassage> Finish(List<Draft> drafts)
    {
        var ordinals = new Dictionary<string, int>();
        var result = new List<ChunkedPassage>(drafts.Count);
        foreach (var (draft, position) in drafts.Select((d, i) => (d, i)))
        {
            var key = string.Join('\u001f', draft.Path);
            var ordinal = ordinals.GetValueOrDefault(key);
            ordinals[key] = ordinal + 1;
            result.Add(new ChunkedPassage(
                position, draft.Path, ordinal, draft.Text,
                draft.PageStart == 0 ? null : draft.PageStart, draft.PageEnd == 0 ? null : draft.PageEnd));
        }

        return result;
    }

    [GeneratedRegex(@"^\s*(`{3,}|~{3,}|\|)", RegexOptions.Multiline)]
    private static partial Regex StructuralLine();

    [GeneratedRegex(@"^ {0,3}(#{1,6})(?:[ \t]+(.*?))?(?:[ \t]+#+)?[ \t]*$")]
    private static partial Regex AtxHeading();

    [GeneratedRegex(@"^ {0,3}(`{3,}|~{3,})")]
    private static partial Regex FenceOpen();

    [GeneratedRegex(@"^\s*\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)*\|?\s*$")]
    private static partial Regex TableSeparator();
}
