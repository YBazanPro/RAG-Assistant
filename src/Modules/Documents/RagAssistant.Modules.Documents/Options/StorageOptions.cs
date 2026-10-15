using System.ComponentModel.DataAnnotations;

namespace RagAssistant.Modules.Documents.Options;

internal sealed class StorageOptions
{
    public const string Section = "Documents:Storage";

    /// <summary>Root of the original-file store. The AppHost passes an absolute path; otherwise it is relative to the content root.</summary>
    [Required]
    public string Path { get; set; } = "./.data/documents";
}
