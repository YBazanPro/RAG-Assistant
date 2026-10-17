using System.Text.RegularExpressions;

namespace RagAssistant.Modules.Documents.Ingestion;

/// <summary>The bits of Markdown syntax that both the Markdown extractor and the Chunker recognise (headings, fences, pipe tables).</summary>
internal static partial class MarkdownSyntax
{
    public static bool IsTableLine(string line) => line.TrimStart().StartsWith('|');

    public static bool TryOpenFence(string line, out (char Char, int Length) fence)
    {
        var match = FenceOpen().Match(line);
        fence = match.Success ? (match.Groups[1].Value[0], match.Groups[1].Length) : default;
        return match.Success;
    }

    public static bool ClosesFence(string line, (char Char, int Length) fence)
    {
        var trimmed = line.Trim();
        return trimmed.Length >= fence.Length && trimmed.All(c => c == fence.Char);
    }

    /// <summary>A line that opens or continues a fence or a table.</summary>
    [GeneratedRegex(@"^\s*(`{3,}|~{3,}|\|)", RegexOptions.Multiline)]
    public static partial Regex StructuralLine();

    [GeneratedRegex(@"^ {0,3}(#{1,6})(?:[ \t]+(.*?))?(?:[ \t]+#+)?[ \t]*$")]
    public static partial Regex AtxHeading();

    [GeneratedRegex(@"^\s*\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)*\|?\s*$")]
    public static partial Regex TableSeparator();

    [GeneratedRegex(@"^ {0,3}(`{3,}|~{3,})")]
    private static partial Regex FenceOpen();
}
