using Mediator;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using RagAssistant.Modules.Chat.Answering;
using RagAssistant.Modules.Chat.Features.AskQuestion;
using RagAssistant.Modules.Chat.Options;
using RagAssistant.SharedKernel.Common;

namespace RagAssistant.Modules.Chat.Features.Admin;

/// <summary>Spec 017: the effective, non-secret Chat options. Only values of the options classes; no connection strings, keys or URLs.</summary>
internal static class GetChatConfiguration
{
    public sealed record Query : IRequest<Result<ChatConfiguration>>;

    internal sealed class Handler(
        IChatClient chat,
        IConfiguration configuration,
        IOptions<GenerationOptions> generation,
        IOptions<ChatRetrievalOptions> retrieval,
        IOptions<FollowUpOptions> followUp,
        IOptions<ConversationOptions> conversations,
        IOptions<FeedbackOptions> feedback,
        IOptions<AdminOptions> admin) : IRequestHandler<Query, Result<ChatConfiguration>>
    {
        public ValueTask<Result<ChatConfiguration>> Handle(Query query, CancellationToken cancellationToken) =>
            ValueTask.FromResult<Result<ChatConfiguration>>(new ChatConfiguration(
                configuration["Chat:Provider"] ?? "Ollama",
                chat.GetService<ChatClientMetadata>()?.DefaultModelId,
                PromptBuilder.PromptVersion,
                PromptBuilder.RewritePromptVersion,
                retrieval.Value.TopK,
                followUp.Value.HistoryExchanges,
                followUp.Value.MaxAnswerChars,
                AcceptQuestion.MaxQuestionLength,
                conversations.Value.MaxMessagesPerConversation,
                feedback.Value.MaxReportsPerAnswer,
                generation.Value.MaxOutputTokens,
                generation.Value.FirstTokenTimeout.TotalSeconds,
                generation.Value.TotalTimeout.TotalSeconds,
                followUp.Value.RewriteTimeout.TotalSeconds,
                !string.IsNullOrWhiteSpace(admin.Value.TraceUrlTemplate)));
    }
}
