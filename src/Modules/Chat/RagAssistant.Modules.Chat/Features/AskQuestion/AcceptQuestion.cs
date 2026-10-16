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

namespace RagAssistant.Modules.Chat.Features.AskQuestion;

/// <summary>
/// Everything that can reject a question happens here, before the response becomes a stream (spec 012): validation, ownership,
/// an answer already in progress, a full conversation. On success the question and a pending answer are committed.
/// </summary>
internal static class AcceptQuestion
{
    /// <summary>The <c>IDocumentSearch</c> query limit; the whole question is always what gets searched.</summary>
    public const int MaxQuestionLength = 1000;
    public const int MaxTags = 20;

    public sealed record Command(string UserId, Guid ConversationId, string? Question, string? Language, string[]? Tags)
        : IRequest<Result<AcceptedAnswer>>;

    public sealed class Validator : AbstractValidator<Command>
    {
        // Same rules as retrieval (spec 011), checked here so a bad filter is a 400 and never a failed answer.
        public Validator()
        {
            RuleFor(x => x.Question)
                .Must(v => !string.IsNullOrWhiteSpace(v))
                .WithMessage("'question' is required.")
                .Must(v => v is null || v.Trim().Length <= MaxQuestionLength)
                .WithMessage($"'question' must be at most {MaxQuestionLength} characters.");

            RuleFor(x => x.Language)
                .Must(v => string.IsNullOrWhiteSpace(v) || (v.Trim().Length == 2 && v.Trim().All(char.IsAsciiLetter)))
                .WithMessage("'language' must be two letters.");

            RuleFor(x => x.Tags)
                .Must(v => v is null || v.Length <= MaxTags)
                .WithMessage($"At most {MaxTags} tags can be combined.");
        }
    }

    internal sealed class Handler(
        ChatDbContext db,
        IOptions<GenerationOptions> generation,
        IOptions<ConversationOptions> conversations,
        TimeProvider clock) : IRequestHandler<Command, Result<AcceptedAnswer>>
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
            if (await PendingAnswers.AnyLiveAsync(db, conversation.Id, cancellationToken))
            {
                return ChatErrors.AnswerInProgress;
            }

            if (conversation.MessageCount + 2 > conversations.Value.MaxMessagesPerConversation)
            {
                return ChatErrors.ConversationFull;
            }

            var question = command.Question!.Trim();
            var language = string.IsNullOrWhiteSpace(command.Language) ? null : command.Language.Trim().ToLowerInvariant();
            var tags = (command.Tags ?? [])
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim().ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            var userMessage = new Message
            {
                Id = Guid.CreateVersion7(now),
                ConversationId = conversation.Id,
                Role = MessageRole.User,
                Sequence = conversation.MessageCount + 1,
                Text = question,
                CreatedAt = now,
                FilterLanguage = language,
                FilterTags = tags,
            };
            var answer = new Message
            {
                Id = Guid.CreateVersion7(now),
                ConversationId = conversation.Id,
                Role = MessageRole.Assistant,
                Sequence = conversation.MessageCount + 2,
                CreatedAt = now,
                ReplyToMessageId = userMessage.Id,
                Status = AnswerStatus.Pending,
                PendingSince = now,
                Attempts = 1,
            };
            db.Messages.AddRange(userMessage, answer);

            conversation.Title ??= ConversationTitle.From(question);
            conversation.MessageCount += 2;
            conversation.LastActivityAt = now;

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new AcceptedAnswer(conversation.Id, userMessage.Id, answer.Id, conversation.OwnerSub, question, language, tags, answer.Attempts);
        }
    }
}
