using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using RagAssistant.Modules.Chat.Common;
using RagAssistant.Modules.Chat.Persistence;
using RagAssistant.SharedKernel.Common;

namespace RagAssistant.Modules.Chat.Features.ListConversations;

internal static class ListConversations
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    /// <param name="Page">Raw text, so a malformed value surfaces as a field error.</param>
    public sealed record Query(string UserId, string? Page, string? PageSize) : IRequest<Result<Page<ConversationSummary>>>;

    public sealed class Validator : AbstractValidator<Query>
    {
        public Validator() => this.AddPagingRules(q => q.Page, q => q.PageSize, MaxPageSize);
    }

    internal sealed class Handler(ChatDbContext db) : IRequestHandler<Query, Result<Page<ConversationSummary>>>
    {
        public async ValueTask<Result<Page<ConversationSummary>>> Handle(Query query, CancellationToken cancellationToken)
        {
            var page = PagingRules.ParsePage(query.Page);
            var pageSize = PagingRules.ParsePageSize(query.PageSize, DefaultPageSize);

            var owned = db.Conversations.AsNoTracking().Where(c => c.OwnerSub == query.UserId);
            var total = await owned.CountAsync(cancellationToken);
            var items = await owned
                .OrderByDescending(c => c.LastActivityAt)
                .ThenBy(c => c.Id)
                .Skip(PagingRules.Skip(page, pageSize))
                .Take(pageSize)
                .Select(c => new ConversationSummary(c.Id, c.Title, c.CreatedAt, c.LastActivityAt))
                .ToListAsync(cancellationToken);

            return new Page<ConversationSummary>(items, page, pageSize, total);
        }
    }
}
