using static RagAssistant.Modules.Documents.Ingestion.MarkdownSyntax;

namespace RagAssistant.Modules.Documents.Ingestion.Extraction;

internal static class LineBlocks
{
    /// <summary>
    /// Cuts lines into blocks at blank lines. With <paramref name="markdown"/>, a fenced code block (blank lines included) and a run
    /// of pipe-table lines are one block each; without it, every block is a paragraph.
    /// </summary>
    public static List<SourceBlock> Parse(IReadOnlyList<string> lines, bool markdown)
    {
        var blocks = new List<SourceBlock>();
        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count > 0)
            {
                blocks.Add(new SourceBlock(BlockKind.Paragraph, paragraph));
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

                blocks.Add(new SourceBlock(BlockKind.Code, code));
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

                blocks.Add(new SourceBlock(BlockKind.Table, table));
                continue;
            }

            paragraph.Add(line);
        }

        FlushParagraph();
        return blocks;
    }
}
