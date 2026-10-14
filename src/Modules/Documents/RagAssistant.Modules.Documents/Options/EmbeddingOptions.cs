using System.ComponentModel.DataAnnotations;

namespace RagAssistant.Modules.Documents.Options;

internal sealed class EmbeddingOptions
{
    public const string Section = "Documents:Embeddings";

    [Required]
    public string Model { get; set; } = "bge-m3";

    /// <summary>Must equal the width of <c>passages.embedding</c>; changing the model needs a migration and re-embedding.</summary>
    public int Dimensions { get; set; } = 1024;
}
