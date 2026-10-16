using Microsoft.EntityFrameworkCore;
using RagAssistant.Modules.Chat.Common;
using RagAssistant.Modules.Chat.Domain;
using RagAssistant.Modules.Chat.Persistence;
using RagAssistant.SharedKernel.Common;

namespace RagAssistant.Modules.Chat.Features.Feedback;

/// <summary>Which message feedback may be given on, and by whom (spec 014).</summary>
internal static class FeedbackTarget
{
    /// <summary>
    /// The owner's final answer: complete, partial or refused. Someone else's conversation or a missing message is "not found";
    /// a user message or an answer that is pending, failed or cancelled is <see cref="ChatErrors.FeedbackNotAllowed"/>.
    /// </summary>
    public static async Task<Result<Message>> FindAsync(
        ChatDbContext db, string userId, Guid conversationId, Guid messageId, CancellationToken cancellationToken)
    {
        var owned = await db.Conversations.AsNoTracking().AnyAsync(c => c.Id == conversationId && c.OwnerSub == userId, cancellationToken);
        if (!owned)
        {
            return ChatErrors.ConversationNotFound;
        }

        var message = await db.Messages.AsNoTracking()
            .SingleOrDefaultAsync(m => m.Id == messageId && m.ConversationId == conversationId, cancellationToken);
        if (message is null)
        {
            return ChatErrors.MessageNotFound;
        }

        return message.Role == MessageRole.Assistant && message.Status is AnswerStatus.Complete or AnswerStatus.Partial or AnswerStatus.Refused
            ? message
            : ChatErrors.FeedbackNotAllowed;
    }
}
