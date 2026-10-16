using System.ComponentModel.DataAnnotations;

namespace RagAssistant.Modules.Chat.Options;

internal sealed class ChatRetrievalOptions
{
    public const string Section = "Chat:Retrieval";

    /// <summary>At most the Documents <c>MaxTopK</c> (20).</summary>
    [Range(1, 20)]
    public int TopK { get; set; } = 8;
}
