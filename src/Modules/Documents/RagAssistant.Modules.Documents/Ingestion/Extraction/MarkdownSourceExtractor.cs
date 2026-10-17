using RagAssistant.Modules.Documents.Domain;
using static RagAssistant.Modules.Documents.Ingestion.MarkdownSyntax;

namespace RagAssistant.Modules.Documents.Ingestion.Extraction;

/// <summary>Markdown versions (spec 007): ATX headings open sections; fenced code and pipe tables are blocks of their own.</summary>
internal sealed class MarkdownSourceExtractor : ISourceExtractor
{
    public SourceFormat Format => SourceFormat.Markdown;

    public Task<SourceExtraction> ExtractAsync(VersionSource source, Action<int, int>? onProgress, CancellationToken cancellationToken)
    {
        var text = source.Content ?? throw new InvalidOperationException("The version has no text content.");
        return Task.FromResult(new SourceExtraction(Parse(text), $"{text.Length} characters"));
    }

    /// <summary>
    /// One section per heading, plus one for the text before the first heading. A heading without body still yields an (empty)
    /// section, so it stays in its descendants' paths. A <c>#</c> line inside a fence is code, not a heading.
    /// </summary>
    public static SourceDocument Parse(string content)
    {
        var sections = new List<SourceSection>();
        var chain = new List<(int Level, string Title)>();
        var path = Array.Empty<string>();
        string? heading = null;
        var body = new List<string>();
        (char Char, int Length)? fence = null;

        void Close() => sections.Add(new SourceSection(path, heading, LineBlocks.Parse(body, markdown: true)));

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
        return new SourceDocument(sections);
    }
}
