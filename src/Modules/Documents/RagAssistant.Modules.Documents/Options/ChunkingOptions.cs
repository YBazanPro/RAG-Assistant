using System.ComponentModel.DataAnnotations;

namespace RagAssistant.Modules.Documents.Options;

internal sealed class ChunkingOptions
{
    public const string Section = "Documents:Chunking";

    /// <summary>Upper bound of a passage, in estimated tokens (ceil(chars / 4)).</summary>
    [Range(1, int.MaxValue)]
    public int MaxTokens { get; set; } = 400;

    [Range(1, int.MaxValue)]
    public int OverlapTokens { get; set; } = 50;

    /// <summary>Passages below this size are merged into a neighbour when possible.</summary>
    [Range(1, int.MaxValue)]
    public int MinTokens { get; set; } = 80;

    public bool IsConsistent => OverlapTokens < MaxTokens && MinTokens < MaxTokens;
}
