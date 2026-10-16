using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RagAssistant.Modules.Chat.Answering;
using RagAssistant.Modules.Chat.Common;
using RagAssistant.Modules.Chat.Domain;
using RagAssistant.Modules.Chat.Options;
using RagAssistant.Modules.Chat.Persistence;
using RagAssistant.SharedKernel.Common;

namespace RagAssistant.Modules.Chat.Features.Feedback;

/// <summary>Adds an issue report to an answer, up to the configured limit (spec 014). Reports are append-only.</summary>
internal static class ReportIssue
{
    public const int MaxCommentLength = 1000;

    public sealed record Command(string UserId, Guid ConversationId, Guid MessageId, string? Category, string? Comment) : IRequest<Result<ReportView>>;

    public sealed record ReportView(Guid Id, string Category, string? Comment, DateTimeOffset CreatedAt);

    public sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(x => x.Category)
                .Must(v => ChatApiNames.TryParseCategory(v, out _))
                .WithMessage("'category' must be one of: incorrect, unsupported_by_sources, wrong_source, outdated, other.");
            RuleFor(x => x.Comment)
                .Must(v => v is null || v.Trim().Length <= MaxCommentLength)
                .WithMessage($"'comment' must be at most {MaxCommentLength} characters.");
        }
    }

    internal sealed class Handler(ChatDbContext db, IOptions<FeedbackOptions> options, TimeProvider clock)
        : IRequestHandler<Command, Result<ReportView>>
    {
        public async ValueTask<Result<ReportView>> Handle(Command command, CancellationToken cancellationToken)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            // The conversation lock makes the count-then-insert below safe against two reports arriving together.
            if (await PendingAnswers.LockOwnedAsync(db, command.ConversationId, command.UserId, cancellationToken) is null)
            {
                return ChatErrors.ConversationNotFound;
            }

            var target = await FeedbackTarget.FindAsync(db, command.UserId, command.ConversationId, command.MessageId, cancellationToken);
            if (target.IsFailure)
            {
                return target.Error;
            }

            if (await db.IssueReports.CountAsync(r => r.MessageId == command.MessageId, cancellationToken) >= options.Value.MaxReportsPerAnswer)
            {
                return ChatErrors.ReportLimit;
            }

            if (!ChatApiNames.TryParseCategory(command.Category, out var category))
            {
                return Error.Validation(Result.ValidationFailedCode, "'category' is not a known category.");
            }

            var now = clock.GetUtcNow();
            var comment = string.IsNullOrWhiteSpace(command.Comment) ? null : command.Comment.Trim();
            var report = new IssueReport { Id = Guid.CreateVersion7(now), MessageId = command.MessageId, Category = category, Comment = comment, CreatedAt = now };
            db.IssueReports.Add(report);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new ReportView(report.Id, category.ToApi(), comment, now);
        }
    }
}
