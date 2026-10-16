using System.Text;

namespace RagAssistant.Modules.Chat.Answering;

/// <summary>
/// Turns the model's raw token stream into the text the user sees (spec 012, pipeline step 7). Two things are held back:
/// the start of the reply while it could still be the sentinel, and an opening bracket while it could still be a citation marker.
/// Markers outside <c>1..k</c> are dropped, lists like <c>[1, 2]</c> become <c>[1][2]</c>, and everything else passes as text.
/// The streamed text and the persisted text are therefore the same.
/// </summary>
internal sealed class AnswerStreamFilter(int sourceCount, string sentinel)
{
    /// <summary>A bracket longer than this is released as text, so the stream cannot stall on prose such as "[see the appendix]".</summary>
    public const int MaxBracketLength = 32;

    private readonly StringBuilder _undecided = new();
    private readonly StringBuilder _bracket = new();
    private readonly StringBuilder _text = new();
    private readonly SortedSet<int> _cited = [];
    private bool _decided;

    public bool SentinelHit { get; private set; }

    public int StrippedMarkerCount { get; private set; }

    /// <summary>Distinct markers that appear in the released text, ascending.</summary>
    public IReadOnlyCollection<int> CitedMarkers => _cited;

    /// <summary>Everything released so far.</summary>
    public string Text => _text.ToString();

    /// <summary>Feeds one fragment; returns the text that can be released now (possibly empty).</summary>
    public string Push(string fragment)
    {
        if (SentinelHit)
        {
            return string.Empty;
        }

        if (!_decided)
        {
            _undecided.Append(fragment);
            var start = _undecided.ToString().TrimStart();
            if (start.Length == 0 || IsSentinelPrefix(start))
            {
                return string.Empty;
            }

            if (IsSentinel(start, complete: false))
            {
                SentinelHit = true;
                return string.Empty;
            }

            _decided = true;
            fragment = _undecided.ToString();
            _undecided.Clear();
        }

        var released = new StringBuilder();
        foreach (var c in fragment)
        {
            Accept(c, released);
        }

        _text.Append(released);
        return released.ToString();
    }

    /// <summary>Ends the stream: an undecided start is checked one last time, and an unclosed bracket is released as text.</summary>
    public string Flush()
    {
        if (SentinelHit)
        {
            return string.Empty;
        }

        var released = new StringBuilder();
        if (!_decided)
        {
            var start = _undecided.ToString().TrimStart();
            _decided = true;
            if (start.Length > 0 && IsSentinel(start, complete: true))
            {
                SentinelHit = true;
                return string.Empty;
            }

            foreach (var c in _undecided.ToString())
            {
                Accept(c, released);
            }

            _undecided.Clear();
        }

        released.Append(_bracket);
        _bracket.Clear();
        _text.Append(released);
        return released.ToString();
    }

    /// <summary>Still undecided: the start is the sentinel or a prefix of it, and what follows decides.</summary>
    private bool IsSentinelPrefix(string start) =>
        start.Length <= sentinel.Length && sentinel.StartsWith(start, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The reply starts with the sentinel, followed by nothing or by a character that cannot continue a word
    /// ("INSUFFICIENT_EVIDENCE." counts, "INSUFFICIENT_EVIDENCES" does not). With nothing after it, the match is final only at the end.
    /// </summary>
    private bool IsSentinel(string start, bool complete)
    {
        if (!start.StartsWith(sentinel, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (start.Length == sentinel.Length)
        {
            return complete;
        }

        var next = start[sentinel.Length];
        return !char.IsLetterOrDigit(next) && next != '_';
    }

    private void Accept(char c, StringBuilder released)
    {
        if (_bracket.Length == 0)
        {
            if (c == '[')
            {
                _bracket.Append(c);
            }
            else
            {
                released.Append(c);
            }

            return;
        }

        if (c == ']')
        {
            _bracket.Append(c);
            ResolveBracket(released);
            return;
        }

        if (char.IsAsciiDigit(c) || c == ',' || c == ' ')
        {
            _bracket.Append(c);
            if (_bracket.Length > MaxBracketLength)
            {
                released.Append(_bracket);
                _bracket.Clear();
            }

            return;
        }

        // Cannot be a marker any more: release what was held, then look at this character afresh (it may open a new bracket).
        released.Append(_bracket);
        _bracket.Clear();
        Accept(c, released);
    }

    private void ResolveBracket(StringBuilder released)
    {
        var content = _bracket.ToString(1, _bracket.Length - 2);
        _bracket.Clear();

        var parts = content.Split(',');
        var numbers = new List<int>(parts.Length);
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0 || !trimmed.All(char.IsAsciiDigit) || !int.TryParse(trimmed, out var n))
            {
                // "[]", "[ ]", "[1,]": not a marker, keep the text as written.
                released.Append('[').Append(content).Append(']');
                return;
            }

            numbers.Add(n);
        }

        foreach (var n in numbers)
        {
            if (n < 1 || n > sourceCount)
            {
                StrippedMarkerCount++;
                continue;
            }

            _cited.Add(n);
            released.Append('[').Append(n).Append(']');
        }
    }
}
