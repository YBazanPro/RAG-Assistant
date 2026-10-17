using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using RagAssistant.Modules.Chat.Domain;
using RagAssistant.Modules.Chat.Persistence;
using RagAssistant.SharedKernel.Common;

namespace RagAssistant.Modules.Chat.Features.Admin;

/// <summary>Spec 017: recent requests of all users, newest first. Decision data only.</summary>
internal static class ListRequests
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;
    public const int QuestionPreviewLength = 120;

    public const string NegativeFeedback = "negative";

    public sealed record Query(IReadOnlyList<string>? Status, string? Feedback, string? Page, string? PageSize) : IRequest<Result<Page<RequestListItem>>>;

    public sealed class Validator : AbstractValidator<Query>
    {
        public Validator()
        {
            this.AddPagingRules(q => q.Page, q => q.PageSize, MaxPageSize);
            RuleForEach(q => q.Status!)
                .Must(s => AdminStatus.TryParse(s, out _))
                .WithMessage("'status' must be one of complete, partial, refused, failed, cancelled.")
                .OverridePropertyName("status")
                .When(q => q.Status is not null);
            RuleFor(q => q.Feedback)
                .Must(f => string.IsNullOrEmpty(f) || f == NegativeFeedback)
                .WithMessage("'feedback' must be 'negative'.")
                .OverridePropertyName("feedback");
        }
    }

    internal sealed class Handler(ChatDbContext db) : IRequestHandler<Query, Result<Page<RequestListItem>>>
    {
        public async ValueTask<Result<Page<RequestListItem>>> Handle(Query query, CancellationToken cancellationToken)
        {
            var page = PagingRules.ParsePage(query.Page);
            var pageSize = PagingRules.ParsePageSize(query.PageSize, DefaultPageSize);

            var traces = db.RequestTraces.AsNoTracking();
            if (query.Status is { Count: > 0 })
            {
                var statuses = query.Status.Select(s => AdminStatus.TryParse(s, out var status) ? status : default).Distinct().ToArray();
                traces = traces.Where(t => statuses.Contains(t.FinalStatus));
            }

            if (query.Feedback == NegativeFeedback)
            {
                traces = traces.Where(t =>
                    db.MessageRatings.Any(r => r.MessageId == t.AssistantMessageId && r.Rating == FeedbackRating.NotHelpful)
                    || db.IssueReports.Any(r => r.MessageId == t.AssistantMessageId));
            }

            var total = await traces.CountAsync(cancellationToken);
            var rows = await traces
                .OrderByDescending(t => t.StartedAt)
                .ThenByDescending(t => t.Id)
                .Skip(PagingRules.Skip(page, pageSize))
                .Take(pageSize)
                .Select(t => new
                {
                    t.Id,
                    t.StartedAt,
                    t.OwnerSub,
                    t.QuestionText,
                    t.FinalStatus,
                    t.RefusalReason,
                    t.ErrorCode,
                    t.EvidenceOutcome,
                    t.BestScore,
                    t.TotalMs,
                    t.Attempt,
                    Negative = db.MessageRatings.Any(r => r.MessageId == t.AssistantMessageId && r.Rating == FeedbackRating.NotHelpful)
                        || db.IssueReports.Any(r => r.MessageId == t.AssistantMessageId),
                })
                .ToListAsync(cancellationToken);

            var items = rows
                .Select(r => new RequestListItem(
                    r.Id, r.StartedAt, r.OwnerSub, Preview(r.QuestionText), r.FinalStatus.ToApi(), r.RefusalReason?.ToApi(), r.ErrorCode,
                    r.EvidenceOutcome, r.BestScore, r.TotalMs, r.Attempt, r.Negative))
                .ToList();
            return new Page<RequestListItem>(items, page, pageSize, total);
        }
    }

    public static string Preview(string question) =>
        question.Length <= QuestionPreviewLength ? question : question[..QuestionPreviewLength] + "…";
}

internal static class AdminStatus
{
    /// <summary>Only the lowercase API spelling of a finished status is accepted; numbers, other casings and <c>pending</c> are not.</summary>
    public static bool TryParse(string? value, out AnswerStatus status)
    {
        status = default;
        if (string.IsNullOrEmpty(value) || !Enum.TryParse(value, ignoreCase: true, out status) || status == AnswerStatus.Pending)
        {
            return false;
        }

        return value == status.ToApi();
    }
}
