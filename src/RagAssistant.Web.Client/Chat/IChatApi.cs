using RagAssistant.Web.Client.Documents;

namespace RagAssistant.Web.Client.Chat;

/// <summary>The chat REST endpoints as the UI uses them. Pages depend on this so component tests can script the API.</summary>
public interface IChatApi
{
    Task<ApiResult<ConversationSummary>> CreateConversationAsync(CancellationToken cancellationToken);

    Task<ApiResult<ConversationPage>> ListConversationsAsync(int page, int pageSize, CancellationToken cancellationToken);

    Task<ApiResult<ConversationDetail>> GetConversationAsync(Guid conversationId, CancellationToken cancellationToken);
}
