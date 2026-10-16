using Mediator;
using RagAssistant.Modules.Chat.Common;
using RagAssistant.Modules.Chat.Domain;
using RagAssistant.Modules.Chat.Persistence;
using RagAssistant.SharedKernel.Common;

namespace RagAssistant.Modules.Chat.Features.CreateConversation;

internal static class CreateConversation
{
    public sealed record Command(string UserId) : IRequest<Result<ConversationSummary>>;

    internal sealed class Handler(ChatDbContext db, TimeProvider clock) : IRequestHandler<Command, Result<ConversationSummary>>
    {
        public async ValueTask<Result<ConversationSummary>> Handle(Command command, CancellationToken cancellationToken)
        {
            var now = clock.GetUtcNow();
            var conversation = new Conversation
            {
                Id = Guid.CreateVersion7(now),
                OwnerSub = command.UserId,
                CreatedAt = now,
                LastActivityAt = now,
            };
            db.Conversations.Add(conversation);
            await db.SaveChangesAsync(cancellationToken);
            return new ConversationSummary(conversation.Id, conversation.Title, conversation.CreatedAt, conversation.LastActivityAt);
        }
    }
}
