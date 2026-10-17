using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using RagAssistant.Modules.Chat.Domain;
using RagAssistant.Modules.Chat.Persistence;
using RagAssistant.SharedKernel.Common;

namespace RagAssistant.Modules.Chat.Features.Admin;

/// <summary>Spec 017: negative ratings and issue reports of all users, newest first, each pointing at the latest attempt of its answer.</summary>
internal static class ListFeedback
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;

    public sealed record Query(string? Page, string? PageSize) : IRequest<Result<Page<FeedbackListItem>>>;

    public sealed class Validator : AbstractValidator<Query>
    {
        public Validator() => this.AddPagingRules(q => q.Page, q => q.PageSize, MaxPageSize);
    }

    private sealed record Row(string Kind, Guid MessageId, IssueCategory? Category, string? Comment, DateTimeOffset CreatedAt);

    internal sealed class Handler(ChatDbContext db) : IRequestHandler<Query, Result<Page<FeedbackListItem>>>
    {
        public async ValueTask<Result<Page<FeedbackListItem>>> Handle(Query query, CancellationToken cancellationToken)
        {
            var page = PagingRules.ParsePage(query.Page);
            var pageSize = PagingRules.ParsePageSize(query.PageSize, DefaultPageSize);

            // Two sources, merged in memory: a SQL union cannot carry the stored-as-text category. Each source needs only its first
            // page * pageSize rows to fill the requested page, so the merge stays bounded.
            var negativeRatings = db.MessageRatings.AsNoTracking().Where(r => r.Rating == FeedbackRating.NotHelpful);
            var reports = db.IssueReports.AsNoTracking();
            var total = await negativeRatings.CountAsync(cancellationToken) + await reports.CountAsync(cancellationToken);
            var take = (int)Math.Min((long)page * pageSize, int.MaxValue);

            var ratingRows = await negativeRatings
                .OrderByDescending(r => r.UpdatedAt).ThenBy(r => r.MessageId).Take(take)
                .Select(r => new Row("rating", r.MessageId, null, null, r.UpdatedAt))
                .ToListAsync(cancellationToken);
            var reportRows = await reports
                .OrderByDescending(r => r.CreatedAt).ThenBy(r => r.MessageId).Take(take)
                .Select(r => new Row("report", r.MessageId, r.Category, r.Comment, r.CreatedAt))
                .ToListAsync(cancellationToken);
            var rows = ratingRows.Concat(reportRows)
                .OrderByDescending(r => r.CreatedAt).ThenBy(r => r.MessageId)
                .Skip(PagingRules.Skip(page, pageSize))
                .Take(pageSize)
                .ToList();

            var messageIds = rows.Select(r => r.MessageId).Distinct().ToArray();
            var traces = (await db.RequestTraces.AsNoTracking()
                    .Where(t => messageIds.Contains(t.AssistantMessageId))
                    .Select(t => new { t.Id, t.AssistantMessageId, t.Attempt, t.QuestionText, t.OwnerSub })
                    .ToListAsync(cancellationToken))
                .GroupBy(t => t.AssistantMessageId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(t => t.Attempt).First());

            var items = rows
                .Select(r =>
                {
                    traces.TryGetValue(r.MessageId, out var trace);
                    return new FeedbackListItem(
                        r.Kind, r.Category?.ToApi(), r.Comment, r.CreatedAt, trace?.Id,
                        trace is null ? null : ListRequests.Preview(trace.QuestionText), trace?.OwnerSub);
                })
                .ToList();
            return new Page<FeedbackListItem>(items, page, pageSize, total);
        }
    }
}
