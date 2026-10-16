using RagAssistant.Modules.Chat.Domain;

namespace RagAssistant.Modules.Chat.Common;

/// <summary>A cited passage as the API shows it: in <c>answer.completed</c> and on assistant messages.</summary>
public sealed record CitationView(
    int Marker,
    Guid PassageId,
    Guid DocumentId,
    Guid VersionId,
    int VersionNumber,
    string DocumentTitle,
    IReadOnlyList<string> SectionPath,
    int? PageStart,
    int? PageEnd,
    double Score,
    string Text)
{
    internal static CitationView From(CitationSnapshot s) =>
        new(s.Marker, s.PassageId, s.DocumentId, s.VersionId, s.VersionNumber, s.DocumentTitle, s.SectionPath, s.PageStart, s.PageEnd, s.Score, s.Text);
}
