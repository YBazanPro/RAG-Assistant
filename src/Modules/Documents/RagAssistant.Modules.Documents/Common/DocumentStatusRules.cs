using RagAssistant.Modules.Documents.Domain;

namespace RagAssistant.Modules.Documents.Common;

internal static class DocumentStatusRules
{
    /// <summary>
    /// The status shown to users: archived wins, then an unfinished or failed latest version, otherwise active.
    /// It is derived, never stored, so it cannot disagree with the version rows.
    /// </summary>
    public static string Derive(DocumentStatus status, VersionStatus? latestVersion) => (status, latestVersion) switch
    {
        (DocumentStatus.Archived, _) => "archived",
        (_, VersionStatus.Queued) => "queued",
        (_, VersionStatus.Processing) => "processing",
        (_, VersionStatus.Failed) => "failed",
        _ => "active",
    };

    public static string ToApi(this VersionStatus status) => status.ToString().ToLowerInvariant();

    public static string ToApi(this RunStatus status) => status.ToString().ToLowerInvariant();

    public static string ToApi(this IngestionStage stage) => stage.ToString().ToLowerInvariant();

    public static string ToApi(this EventStatus status) => status.ToString().ToLowerInvariant();

    public static string ToApi(this SourceFormat format) => format.ToString().ToLowerInvariant();
}
