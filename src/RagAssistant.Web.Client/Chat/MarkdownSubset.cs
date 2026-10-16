namespace RagAssistant.Web.Client.Chat;

/// <summary>An inline run of an answer: literal text, bold, italic, or a <c>[n]</c> citation marker.</summary>
public abstract record Inline;

public sealed record TextRun(string Text) : Inline;

public sealed record BoldRun(IReadOnlyList<Inline> Children) : Inline;

public sealed record ItalicRun(IReadOnlyList<Inline> Children) : Inline;

public sealed record MarkerRun(int Marker) : Inline;

/// <summary>A block of an answer. Each paragraph or list item is a list of lines; lines are separated by line breaks.</summary>
public abstract record Block;

public sealed record Paragraph(IReadOnlyList<IReadOnlyList<Inline>> Lines) : Block;

public sealed record ListBlock(bool Ordered, IReadOnlyList<IReadOnlyList<Inline>> Items) : Block;

/// <summary>
/// The small Markdown subset answers may use (spec 013): paragraphs, one-level lists, bold, italic and citation markers.
/// Everything else, including headings, links, code, tables and HTML, stays literal text. The result is rendered as Razor
/// elements, so nothing here ever produces markup.
/// </summary>
public static class MarkdownSubset
{
    private const int MaxMarkerDigits = 4;

    public static IReadOnlyList<Block> Parse(string text)
    {
        var blocks = new List<Block>();
        List<IReadOnlyList<Inline>>? lines = null;
        bool? ordered = null;

        void Flush()
        {
            if (lines is { Count: > 0 })
            {
                blocks.Add(ordered is { } o ? new ListBlock(o, lines) : new Paragraph(lines));
            }

            lines = null;
            ordered = null;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
            {
                Flush();
                continue;
            }

            var bullet = ListMarkerLength(line, out var isOrdered);
            if (bullet > 0)
            {
                if (ordered != isOrdered)
                {
                    Flush();
                    ordered = isOrdered;
                }

                (lines ??= []).Add(ParseInline(line[bullet..]));
                continue;
            }

            if (ordered is not null)
            {
                Flush();
            }

            (lines ??= []).Add(ParseInline(line));
        }

        Flush();
        return blocks;
    }

    // "- x", "* x" or "1. x" at the start of a line (leading spaces allowed); returns the length of the marker, or 0.
    private static int ListMarkerLength(string line, out bool ordered)
    {
        ordered = false;
        var i = 0;
        while (i < line.Length && line[i] == ' ')
        {
            i++;
        }

        if (i + 1 < line.Length && line[i] is '-' or '*' && line[i + 1] == ' ')
        {
            return i + 2;
        }

        var digits = i;
        while (digits < line.Length && char.IsAsciiDigit(line[digits]))
        {
            digits++;
        }

        if (digits > i && digits - i <= 9 && digits + 1 < line.Length && line[digits] == '.' && line[digits + 1] == ' ')
        {
            ordered = true;
            return digits + 2;
        }

        return 0;
    }

    public static IReadOnlyList<Inline> ParseInline(string text)
    {
        var result = new List<Inline>();
        var literal = new System.Text.StringBuilder();

        void FlushLiteral()
        {
            if (literal.Length > 0)
            {
                result.Add(new TextRun(literal.ToString()));
                literal.Clear();
            }
        }

        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var close = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (close > i + 2)
                {
                    FlushLiteral();
                    result.Add(new BoldRun(ParseInline(text[(i + 2)..close])));
                    i = close + 2;
                }
                else
                {
                    // An unmatched pair stays literal; skipping both stars keeps them from being read as italic.
                    literal.Append("**");
                    i += 2;
                }
            }
            else if (c == '*')
            {
                var close = text.IndexOf('*', i + 1);
                if (close > i + 1 && !char.IsWhiteSpace(text[i + 1]) && !char.IsWhiteSpace(text[close - 1]))
                {
                    FlushLiteral();
                    result.Add(new ItalicRun(ParseInline(text[(i + 1)..close])));
                    i = close + 1;
                }
                else
                {
                    literal.Append(c);
                    i++;
                }
            }
            else if (c == '[' && TryReadMarker(text, i, out var marker, out var length))
            {
                FlushLiteral();
                result.Add(new MarkerRun(marker));
                i += length;
            }
            else
            {
                literal.Append(c);
                i++;
            }
        }

        FlushLiteral();
        return result;
    }

    private static bool TryReadMarker(string text, int start, out int marker, out int length)
    {
        marker = 0;
        length = 0;
        var end = start + 1;
        while (end < text.Length && char.IsAsciiDigit(text[end]) && end - start - 1 < MaxMarkerDigits)
        {
            end++;
        }

        if (end == start + 1 || end >= text.Length || text[end] != ']')
        {
            return false;
        }

        marker = int.Parse(text.AsSpan(start + 1, end - start - 1), System.Globalization.CultureInfo.InvariantCulture);
        length = end - start + 1;
        return marker > 0;
    }
}
