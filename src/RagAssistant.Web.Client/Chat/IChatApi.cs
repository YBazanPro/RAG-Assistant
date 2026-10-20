using RagAssistant.Web.Client.Documents;

namespace RagAssistant.Web.Client.Chat;

/// <summary>The chat REST endpoints as the UI uses them. Pages depend on this so component tests can script the API.</summary>
public interface IChatApi
{
    Task<ApiResult<ConversationSummary>> CreateConversationAsync(CancellationToken cancellationToken);

    /// <param name="search">Spec 020: only conversations whose title or a question contains it, ignoring case and accents; null for all.</param>
    Task<ApiResult<ConversationPage>> ListConversationsAsync(int page, int pageSize, string? search, CancellationToken cancellationToken);

    /// <summary>Spec 020: an empty or blank title goes back to the first question.</summary>
    Task<ApiResult<ConversationSummary>> RenameConversationAsync(Guid conversationId, string title, CancellationToken cancellationToken);

    Task<ApiResult<ConversationDetail>> GetConversationAsync(Guid conversationId, CancellationToken cancellationToken);

    Task<ApiResult<Done>> DeleteConversationAsync(Guid conversationId, CancellationToken cancellationToken);

    /// <param name="rating"><c>helpful</c> or <c>not_helpful</c>.</param>
    Task<ApiResult<RatingResult>> SetRatingAsync(Guid conversationId, Guid messageId, string rating, CancellationToken cancellationToken);

    /// <summary>Replaces the documents and tags the conversation searches; both empty means the whole corpus.</summary>
    Task<ApiResult<ConversationScope>> SetScopeAsync(
        Guid conversationId, IReadOnlyList<Guid> documentIds, IReadOnlyList<string> tags, CancellationToken cancellationToken);

    Task<ApiResult<ActivityView>> GetActivityAsync(Guid conversationId, Guid messageId, CancellationToken cancellationToken);

    Task<ApiResult<Done>> ClearRatingAsync(Guid conversationId, Guid messageId, CancellationToken cancellationToken);

    Task<ApiResult<IssueReportResult>> ReportIssueAsync(
        Guid conversationId, Guid messageId, string category, string? comment, CancellationToken cancellationToken);
}
