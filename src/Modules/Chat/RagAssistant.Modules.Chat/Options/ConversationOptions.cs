using System.ComponentModel.DataAnnotations;

namespace RagAssistant.Modules.Chat.Options;

internal sealed class ConversationOptions
{
    public const string Section = "Chat:Conversations";

    /// <summary>200 messages are 100 exchanges; keeps a full conversation response bounded.</summary>
    [Range(2, 10_000)]
    public int MaxMessagesPerConversation { get; set; } = 200;
}
