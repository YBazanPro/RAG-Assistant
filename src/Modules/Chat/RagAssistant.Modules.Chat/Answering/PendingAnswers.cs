using Microsoft.EntityFrameworkCore;
using RagAssistant.Modules.Chat.Common;
using RagAssistant.Modules.Chat.Domain;
using RagAssistant.Modules.Chat.Persistence;

namespace RagAssistant.Modules.Chat.Answering;

/// <summary>The rules every new attempt passes before it may start (spec 012, pipeline step 1).</summary>
internal static class PendingAnswers
{
    /// <summary>Locks the conversation row until the transaction ends, so two asks in the same conversation cannot both pass the checks.</summary>
    public static Task<Conversation?> LockOwnedAsync(ChatDbContext db, Guid conversationId, string ownerSub, CancellationToken cancellationToken) =>
        db.Conversations
            .FromSql($"SELECT * FROM chat.conversations WHERE id = {conversationId} AND owner_sub = {ownerSub} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    /// <summary>
    /// A pending answer older than the longest possible attempt was left behind by a crash; it must not hold the conversation
    /// forever (spec 012, stale answers).
    /// </summary>
    public static Task<int> FailStaleAsync(ChatDbContext db, Guid conversationId, DateTimeOffset now, TimeSpan staleAfter, CancellationToken cancellationToken)
    {
        var cutoff = now - staleAfter;
        return db.Messages
            .Where(m => m.ConversationId == conversationId && m.Status == AnswerStatus.Pending && m.PendingSince < cutoff)
            .ExecuteUpdateAsync(
                s => s.SetProperty(m => m.Status, AnswerStatus.Failed)
                    .SetProperty(m => m.ErrorCode, ChatErrors.InterruptedCode)
                    .SetProperty(m => m.CompletedAt, now),
                cancellationToken);
    }

    public static Task<bool> AnyLiveAsync(ChatDbContext db, Guid conversationId, CancellationToken cancellationToken) =>
        db.Messages.AnyAsync(m => m.ConversationId == conversationId && m.Status == AnswerStatus.Pending, cancellationToken);
}
