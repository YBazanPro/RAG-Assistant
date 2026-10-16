using RagAssistant.Web.Client.Documents;

namespace RagAssistant.Web.Client.Chat;

/// <summary>The chat REST endpoints as the UI uses them. Pages depend on this so component tests can script the API.</summary>
public interface IChatApi
{
    Task<ApiResult<ConversationSummary>> CreateConversationAsync(CancellationToken cancellationToken);

    Task<ApiResult<ConversationPage>> ListConversationsAsync(int page, int pageSize, CancellationToken cancellationToken);

    Task<ApiResult<ConversationDetail>> GetConversationAsync(Guid conversationId, CancellationToken cancellationToken);

    Task<ApiResult<Done>> DeleteConversationAsync(Guid conversationId, CancellationToken cancellationToken);

    /// <param name="rating"><c>helpful</c> or <c>not_helpful</c>.</param>
    Task<ApiResult<RatingResult>> SetRatingAsync(Guid conversationId, Guid messageId, string rating, CancellationToken cancellationToken);

    Task<ApiResult<ActivityView>> GetActivityAsync(Guid conversationId, Guid messageId, CancellationToken cancellationToken);

    Task<ApiResult<Done>> ClearRatingAsync(Guid conversationId, Guid messageId, CancellationToken cancellationToken);

    Task<ApiResult<IssueReportResult>> ReportIssueAsync(
        Guid conversationId, Guid messageId, string category, string? comment, CancellationToken cancellationToken);
}
