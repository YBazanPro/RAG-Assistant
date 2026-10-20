using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using RagAssistant.Modules.Chat.Common;
using RagAssistant.Modules.Chat.Domain;
using RagAssistant.Modules.Chat.Persistence;
using RagAssistant.SharedKernel.Common;

namespace RagAssistant.Modules.Chat.Features.RenameConversation;

/// <summary>The owner names a conversation (spec 020); an empty or blank title goes back to the one taken from the first question.</summary>
internal static class RenameConversation
{
    public sealed record Command(string UserId, Guid ConversationId, string? Title) : IRequest<Result<ConversationSummary>>;

    public sealed class Validator : AbstractValidator<Command>
    {
        public Validator() =>
            RuleFor(c => c.Title)
                .Must(t => (t ?? "").Trim().Length <= ConversationTitle.MaxRenamedLength)
                .WithMessage($"The title is at most {ConversationTitle.MaxRenamedLength} characters.");
    }

    internal sealed class Handler(ChatDbContext db) : IRequestHandler<Command, Result<ConversationSummary>>
    {
        public async ValueTask<Result<ConversationSummary>> Handle(Command command, CancellationToken cancellationToken)
        {
            var conversation = await db.Conversations
                .FirstOrDefaultAsync(c => c.Id == command.ConversationId && c.OwnerSub == command.UserId, cancellationToken);
            if (conversation is null)
            {
                return ChatErrors.ConversationNotFound;
            }

            var title = command.Title?.Trim();
            if (string.IsNullOrEmpty(title))
            {
                var firstQuestion = await db.Messages.AsNoTracking()
                    .Where(m => m.ConversationId == conversation.Id && m.Role == MessageRole.User)
                    .OrderBy(m => m.Sequence)
                    .Select(m => m.Text)
                    .FirstOrDefaultAsync(cancellationToken);
                conversation.Title = firstQuestion is null ? null : ConversationTitle.From(firstQuestion);
                conversation.TitleEdited = false;
            }
            else
            {
                conversation.Title = title;
                conversation.TitleEdited = true;
            }

            // A rename is not activity: the list keeps its order.
            await db.SaveChangesAsync(cancellationToken);
            return new ConversationSummary(conversation.Id, conversation.Title, conversation.CreatedAt, conversation.LastActivityAt, conversation.TitleEdited);
        }
    }
}
