using System.ComponentModel.DataAnnotations;

namespace RagAssistant.Modules.Documents.Options;

internal sealed class ImportOptions
{
    public const string Section = "Documents:Import";

    /// <summary>Largest accepted upload. Whole requests are capped slightly above it to leave room for the form fields.</summary>
    [Range(1, int.MaxValue)]
    public int MaxFileSizeBytes { get; set; } = 1_048_576;
}
