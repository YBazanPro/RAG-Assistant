using Mediator;
using Microsoft.EntityFrameworkCore;
using RagAssistant.Modules.Chat.Common;
using RagAssistant.Modules.Chat.Domain;
using RagAssistant.Modules.Chat.Persistence;
using RagAssistant.SharedKernel.Common;

namespace RagAssistant.Modules.Chat.Features.GetConversation;

internal static class GetConversation
{
    public sealed record Query(string UserId, Guid ConversationId) : IRequest<Result<ConversationDetail>>;

    public sealed record MessageFilters(string? Language, string[] Tags);

    /// <param name="Filters">User messages only.</param>
    /// <param name="Retryable">Derived from <paramref name="ErrorCode"/>; null unless the status is <c>failed</c>.</param>
    public sealed record MessageView(
        Guid Id,
        string Role,
        int Sequence,
        string Text,
        DateTimeOffset CreatedAt,
        MessageFilters? Filters,
        string? RetrievalQuery,
        Guid? ReplyToMessageId,
        string? Status,
        string? RefusalReason,
        string? ErrorCode,
        bool? Retryable,
        DateTimeOffset? CompletedAt,
        IReadOnlyList<CitationView>? Citations,
        string? Rating = null,
        int? ReportCount = null);

    public sealed record ConversationDetail(
        Guid Id, string? Title, DateTimeOffset CreatedAt, DateTimeOffset LastActivityAt, IReadOnlyList<MessageView> Messages);

    internal sealed class Handler(ChatDbContext db) : IRequestHandler<Query, Result<ConversationDetail>>
    {
        public async ValueTask<Result<ConversationDetail>> Handle(Query query, CancellationToken cancellationToken)
        {
            // Someone else's conversation is "not found", for maintainers too: its existence must not leak.
            var conversation = await db.Conversations.AsNoTracking()
                .Where(c => c.Id == query.ConversationId && c.OwnerSub == query.UserId)
                .Include(c => c.Messages.OrderBy(m => m.Sequence))
                .ThenInclude(m => m.Citations.OrderBy(s => s.Marker))
                .AsSplitQuery()
                .FirstOrDefaultAsync(cancellationToken);
            if (conversation is null)
            {
                return ChatErrors.ConversationNotFound;
            }

            var answerIds = conversation.Messages.Where(m => m.Role == MessageRole.Assistant).Select(m => m.Id).ToList();
            var ratings = await db.MessageRatings.AsNoTracking()
                .Where(r => answerIds.Contains(r.MessageId))
                .ToDictionaryAsync(r => r.MessageId, r => r.Rating, cancellationToken);
            var reportCounts = await db.IssueReports.AsNoTracking()
                .Where(r => answerIds.Contains(r.MessageId))
                .GroupBy(r => r.MessageId)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(g => g.Key, g => g.Count, cancellationToken);

            return new ConversationDetail(
                conversation.Id,
                conversation.Title,
                conversation.CreatedAt,
                conversation.LastActivityAt,
                [.. conversation.Messages.Select(m => ToView(m, ratings, reportCounts))]);
        }

        private static MessageView ToView(Message m, Dictionary<Guid, FeedbackRating> ratings, Dictionary<Guid, int> reportCounts) => m.Role == MessageRole.User
            ? new MessageView(
                m.Id, m.Role.ToApi(), m.Sequence, m.Text, m.CreatedAt, new MessageFilters(m.FilterLanguage, m.FilterTags),
                m.RetrievalQuery ?? m.Text, null, null, null, null, null, null, null)
            : new MessageView(
                m.Id, m.Role.ToApi(), m.Sequence, m.Text, m.CreatedAt, null, null, m.ReplyToMessageId,
                m.Status?.ToApi(),
                m.RefusalReason?.ToApi(),
                m.ErrorCode,
                m.Status == AnswerStatus.Failed && m.ErrorCode is { } code ? ChatErrors.IsRetryable(code) : null,
                m.CompletedAt,
                [.. m.Citations.Select(CitationView.From)],
                ratings.TryGetValue(m.Id, out var rating) ? rating.ToApi() : null,
                reportCounts.GetValueOrDefault(m.Id));
    }
}
