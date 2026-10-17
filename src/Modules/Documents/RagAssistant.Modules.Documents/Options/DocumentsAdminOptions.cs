using System.ComponentModel.DataAnnotations;

namespace RagAssistant.Modules.Documents.Options;

/// <summary>Settings of the maintainers' corpus views (spec 017).</summary>
internal sealed class DocumentsAdminOptions
{
    public const string Section = "Documents:Admin";

    /// <summary>An active shared document is stale when its latest version is older than this many days.</summary>
    [Range(1, 36_500)]
    public int StaleAfterDays { get; set; } = 180;
}
