using RagAssistant.Modules.Chat.Domain;
using RagAssistant.SharedKernel.Contracts.Documents;
using SourceStateValue = RagAssistant.SharedKernel.Contracts.Documents.SourceState;

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
    string Text,
    bool DocumentReadable = true,
    string? Language = null,
    string SourceState = SourceStateNames.Current)
{
    /// <param name="documentReadable">False when the viewer can no longer read the live document (spec 016): the snapshot is then the only source.</param>
    /// <param name="state">How the source stands today (spec 020); a live answer cites the active versions, so <c>current</c>.</param>
    internal static CitationView From(CitationSnapshot s, bool documentReadable = true, SourceStateValue state = SourceStateValue.Current) =>
        new(s.Marker, s.PassageId, s.DocumentId, s.VersionId, s.VersionNumber, s.DocumentTitle, s.SectionPath, s.PageStart, s.PageEnd, s.Score, s.Text,
            documentReadable, s.Language, state.ToApi());
}

/// <summary>The wire names of <see cref="SourceState"/> (spec 020).</summary>
public static class SourceStateNames
{
    public const string Current = "current";
    public const string NewerUnchanged = "newer-unchanged";
    public const string NewerChanged = "newer-changed";
    public const string Archived = "archived";
    public const string Unavailable = "unavailable";

    internal static string ToApi(this SourceState state) => state switch
    {
        SourceState.NewerUnchanged => NewerUnchanged,
        SourceState.NewerChanged => NewerChanged,
        SourceState.Archived => Archived,
        SourceState.Unavailable => Unavailable,
        _ => Current,
    };
}
