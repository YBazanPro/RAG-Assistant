using System.ComponentModel.DataAnnotations;

namespace RagAssistant.Modules.Chat.Options;

internal sealed class FeedbackOptions
{
    public const string Section = "Chat:Feedback";

    [Range(1, int.MaxValue)]
    public int MaxReportsPerAnswer { get; set; } = 10;
}
