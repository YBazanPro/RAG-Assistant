using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using RagAssistant.Modules.Chat.Common;
using RagAssistant.Modules.Chat.Domain;
using RagAssistant.Modules.Chat.Persistence;
using RagAssistant.SharedKernel.Common;

namespace RagAssistant.Modules.Chat.Features.ListConversations;

internal static class ListConversations
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    public const int MaxSearchLength = 200;

    /// <param name="Page">Raw text, so a malformed value surfaces as a field error.</param>
    /// <param name="Search">Spec 020: keeps conversations whose title or a question contains it, ignoring case and accents.</param>
    public sealed record Query(string UserId, string? Page, string? PageSize, string? Search = null) : IRequest<Result<Page<ConversationSummary>>>;

    public sealed class Validator : AbstractValidator<Query>
    {
        public Validator()
        {
            this.AddPagingRules(q => q.Page, q => q.PageSize, MaxPageSize);
            RuleFor(q => q.Search)
                .Must(s => s is null || s.Length <= MaxSearchLength)
                .WithMessage($"'q' is at most {MaxSearchLength} characters.")
                .OverridePropertyName("q");
        }
    }

    internal sealed class Handler(ChatDbContext db) : IRequestHandler<Query, Result<Page<ConversationSummary>>>
    {
        public async ValueTask<Result<Page<ConversationSummary>>> Handle(Query query, CancellationToken cancellationToken)
        {
            var page = PagingRules.ParsePage(query.Page);
            var pageSize = PagingRules.ParsePageSize(query.PageSize, DefaultPageSize);

            var owned = db.Conversations.AsNoTracking().Where(c => c.OwnerSub == query.UserId);
            if (query.Search?.Trim() is { Length: > 0 } search)
            {
                // A sequential scan over one owner's rows (spec 020, deferred index); the pattern's own wildcards are escaped.
                var pattern = "%" + search.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal)
                    .Replace("_", @"\_", StringComparison.Ordinal) + "%";
                owned = owned.Where(c =>
                    EF.Functions.ILike(EF.Functions.Unaccent(c.Title ?? ""), EF.Functions.Unaccent(pattern), @"\")
                    || c.Messages.Any(m => m.Role == MessageRole.User
                        && EF.Functions.ILike(EF.Functions.Unaccent(m.Text), EF.Functions.Unaccent(pattern), @"\")));
            }

            var total = await owned.CountAsync(cancellationToken);
            var items = await owned
                .OrderByDescending(c => c.LastActivityAt)
                .ThenBy(c => c.Id)
                .Skip(PagingRules.Skip(page, pageSize))
                .Take(pageSize)
                .Select(c => new ConversationSummary(c.Id, c.Title, c.CreatedAt, c.LastActivityAt, c.TitleEdited))
                .ToListAsync(cancellationToken);

            return new Page<ConversationSummary>(items, page, pageSize, total);
        }
    }
}
