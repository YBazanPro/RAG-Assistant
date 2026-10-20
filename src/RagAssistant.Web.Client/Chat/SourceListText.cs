using System.Globalization;
using System.Text;

namespace RagAssistant.Web.Client.Chat;

/// <summary>
/// An answer with its numbered sources, as "Copy answer with sources" puts it on the clipboard (spec 020). The source lines are the
/// export's (Chat module, <c>ExportConversation.SourceList</c>), so keep both in step.
/// </summary>
public static class SourceListText
{
    public static string AnswerWithSources(string answerText, IEnumerable<Citation> cited)
    {
        var text = new StringBuilder(answerText.TrimEnd());
        var lines = cited.OrderBy(c => c.Marker).Select(Line).ToList();
        if (lines.Count > 0)
        {
            text.Append("\n\nSources:\n\n").AppendJoin('\n', lines);
        }

        return text.ToString();
    }

    public static string Line(Citation c)
    {
        if (c.SourceState == SourceStates.Unavailable)
        {
            return string.Create(CultureInfo.InvariantCulture, $"- [{c.Marker}] Source no longer available");
        }

        var line = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"- [{c.Marker}] {c.DocumentTitle}"));
        if (c.SectionPath.Count > 0)
        {
            line.Append(" — ").Append(CitationText.Section(c));
        }

        line.Append(CultureInfo.InvariantCulture, $" (version {c.VersionNumber}");
        if (c.PageStart is { } start)
        {
            line.Append(c.PageEnd is { } end && end > start
                ? string.Create(CultureInfo.InvariantCulture, $", pages {start}–{end}")
                : string.Create(CultureInfo.InvariantCulture, $", page {start}"));
        }

        line.Append(')');
        if (SourceStates.ListNote(c.SourceState) is { } note)
        {
            line.Append(" — ").Append(note);
        }

        return line.ToString();
    }
}
