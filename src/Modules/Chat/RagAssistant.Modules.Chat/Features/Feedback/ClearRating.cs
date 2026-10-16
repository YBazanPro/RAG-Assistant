using Mediator;
using Microsoft.EntityFrameworkCore;
using RagAssistant.Modules.Chat.Persistence;
using RagAssistant.SharedKernel.Common;

namespace RagAssistant.Modules.Chat.Features.Feedback;

/// <summary>Removes the rating of an answer; it is not an error when there was none (spec 014).</summary>
internal static class ClearRating
{
    public sealed record Command(string UserId, Guid ConversationId, Guid MessageId) : IRequest<Result>;

    internal sealed class Handler(ChatDbContext db) : IRequestHandler<Command, Result>
    {
        public async ValueTask<Result> Handle(Command command, CancellationToken cancellationToken)
        {
            var target = await FeedbackTarget.FindAsync(db, command.UserId, command.ConversationId, command.MessageId, cancellationToken);
            if (target.IsFailure)
            {
                return Result.Failure(target.Error);
            }

            await db.MessageRatings.Where(r => r.MessageId == command.MessageId).ExecuteDeleteAsync(cancellationToken);
            return Result.Success();
        }
    }
}
