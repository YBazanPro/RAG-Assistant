using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RagAssistant.Modules.Chat.Answering;
using RagAssistant.Modules.Chat.Common;
using RagAssistant.Modules.Chat.Domain;
using RagAssistant.Modules.Chat.Options;
using RagAssistant.Modules.Chat.Persistence;
using RagAssistant.SharedKernel.Common;

namespace RagAssistant.Modules.Chat.Features.RetryAnswer;

/// <summary>
/// Restarts the last answer of a conversation when it failed or was cancelled (spec 012). The same assistant message is reused,
/// so the history keeps one answer per question; every attempt still gets its own trace row.
/// </summary>
internal static class AcceptRetry
{
    public sealed record Command(string UserId, Guid ConversationId, Guid AssistantMessageId) : IRequest<Result<AcceptedAnswer>>;

    internal sealed class Handler(ChatDbContext db, IOptions<GenerationOptions> generation, TimeProvider clock)
        : IRequestHandler<Command, Result<AcceptedAnswer>>
    {
        public async ValueTask<Result<AcceptedAnswer>> Handle(Command command, CancellationToken cancellationToken)
        {
            var now = clock.GetUtcNow();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            var conversation = await PendingAnswers.LockOwnedAsync(db, command.ConversationId, command.UserId, cancellationToken);
            if (conversation is null)
            {
                return ChatErrors.ConversationNotFound;
            }

            await PendingAnswers.FailStaleAsync(db, conversation.Id, now, generation.Value.StaleAfter, cancellationToken);

            var answer = await db.Messages
                .Include(m => m.Citations)
                .SingleOrDefaultAsync(
                    m => m.Id == command.AssistantMessageId && m.ConversationId == conversation.Id && m.Role == MessageRole.Assistant,
                    cancellationToken);
            if (answer is null)
            {
                return ChatErrors.MessageNotFound;
            }

            if (await PendingAnswers.AnyLiveAsync(db, conversation.Id, cancellationToken))
            {
                return ChatErrors.AnswerInProgress;
            }

            if (answer.Sequence != conversation.MessageCount || answer.Status is not (AnswerStatus.Failed or AnswerStatus.Cancelled))
            {
                return ChatErrors.RetryNotAllowed;
            }

            var question = await db.Messages.SingleAsync(m => m.Id == answer.ReplyToMessageId, cancellationToken);

            answer.Status = AnswerStatus.Pending;
            answer.Text = string.Empty;
            answer.ErrorCode = null;
            answer.RefusalReason = null;
            answer.CompletedAt = null;
            answer.PendingSince = now;
            answer.Attempts++;
            answer.Citations.Clear();

            // The answer is about to change, so a verdict on the old one no longer applies (spec 014; in practice failed and
            // cancelled answers cannot have feedback, this is the defensive rule).
            await db.MessageRatings.Where(r => r.MessageId == answer.Id).ExecuteDeleteAsync(cancellationToken);
            await db.IssueReports.Where(r => r.MessageId == answer.Id).ExecuteDeleteAsync(cancellationToken);
            conversation.LastActivityAt = now;

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new AcceptedAnswer(
                conversation.Id, question.Id, answer.Id, conversation.OwnerSub, question.Text, question.FilterLanguage, question.FilterTags, question.FilterDocumentIds, answer.Attempts, question.Sequence);
        }
    }
}
