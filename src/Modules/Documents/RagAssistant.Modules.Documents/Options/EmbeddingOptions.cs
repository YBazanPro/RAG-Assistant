using System.ComponentModel.DataAnnotations;

namespace RagAssistant.Modules.Documents.Options;

internal sealed class EmbeddingOptions
{
    public const string Section = "Documents:Embeddings";

    /// <summary>Fallback only: the model recorded on a version is the one the provider reports (PassageEmbedder.ModelId).</summary>
    [Required]
    public string Model { get; set; } = "bge-m3";

    /// <summary>Must equal the width of <c>passages.embedding</c>; changing the model needs a migration and re-embedding.</summary>
    public int Dimensions { get; set; } = 1024;
}
