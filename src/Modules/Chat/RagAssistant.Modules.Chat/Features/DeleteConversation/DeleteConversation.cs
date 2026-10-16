using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RagAssistant.Modules.Chat.Answering;
using RagAssistant.Modules.Chat.Common;
using RagAssistant.Modules.Chat.Options;
using RagAssistant.Modules.Chat.Persistence;
using RagAssistant.SharedKernel.Common;

namespace RagAssistant.Modules.Chat.Features.DeleteConversation;

/// <summary>
/// Hard-deletes a conversation with everything that hangs off it (spec 014, ADR 0004 D4): messages, citation snapshots, ratings,
/// issue reports and request traces. The foreign keys cascade, so one statement in one transaction leaves nothing behind. Traces
/// hold the question text, which is why they go too.
/// </summary>
internal static class DeleteConversation
{
    public sealed record Command(string UserId, Guid ConversationId) : IRequest<Result>;

    internal sealed class Handler(ChatDbContext db, IOptions<GenerationOptions> generation, TimeProvider clock) : IRequestHandler<Command, Result>
    {
        public async ValueTask<Result> Handle(Command command, CancellationToken cancellationToken)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            // The same lock the ask handlers take: a second DELETE waits here and then finds nothing (404).
            var conversation = await PendingAnswers.LockOwnedAsync(db, command.ConversationId, command.UserId, cancellationToken);
            if (conversation is null)
            {
                return Result.Failure(ChatErrors.ConversationNotFound);
            }

            await PendingAnswers.FailStaleAsync(db, conversation.Id, clock.GetUtcNow(), generation.Value.StaleAfter, cancellationToken);
            if (await PendingAnswers.AnyLiveAsync(db, conversation.Id, cancellationToken))
            {
                return Result.Failure(ChatErrors.AnswerInProgress);
            }

            await db.Conversations.Where(c => c.Id == conversation.Id).ExecuteDeleteAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Result.Success();
        }
    }
}
