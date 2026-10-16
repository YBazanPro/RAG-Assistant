using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using RagAssistant.Modules.Chat.Domain;
using RagAssistant.Modules.Chat.Persistence;
using RagAssistant.SharedKernel.Common;

namespace RagAssistant.Modules.Chat.Features.Feedback;

/// <summary>Sets or replaces the rating of an answer; setting the same one again is fine (spec 014).</summary>
internal static class SetRating
{
    public sealed record Command(string UserId, Guid ConversationId, Guid MessageId, string? Rating) : IRequest<Result<RatingView>>;

    public sealed record RatingView(string Rating, DateTimeOffset UpdatedAt);

    public sealed class Validator : AbstractValidator<Command>
    {
        public Validator() => RuleFor(x => x.Rating)
            .Must(v => ChatApiNames.TryParseRating(v, out _))
            .WithMessage("'rating' must be 'helpful' or 'not_helpful'.");
    }

    internal sealed class Handler(ChatDbContext db, TimeProvider clock) : IRequestHandler<Command, Result<RatingView>>
    {
        public async ValueTask<Result<RatingView>> Handle(Command command, CancellationToken cancellationToken)
        {
            var target = await FeedbackTarget.FindAsync(db, command.UserId, command.ConversationId, command.MessageId, cancellationToken);
            if (target.IsFailure)
            {
                return target.Error;
            }

            if (!ChatApiNames.TryParseRating(command.Rating, out var rating))
            {
                return Error.Validation(Result.ValidationFailedCode, "'rating' must be 'helpful' or 'not_helpful'.");
            }

            var now = clock.GetUtcNow();
            var messageId = command.MessageId;

            if (await UpdateAsync(messageId, rating, now, cancellationToken) == 0)
            {
                db.MessageRatings.Add(new MessageRating { MessageId = messageId, Rating = rating, UpdatedAt = now });
                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException)
                {
                    // A concurrent first rating won the insert; this one replaces it.
                    db.ChangeTracker.Clear();
                    await UpdateAsync(messageId, rating, now, cancellationToken);
                }
            }

            return new RatingView(rating.ToApi(), now);
        }

        private Task<int> UpdateAsync(Guid messageId, FeedbackRating rating, DateTimeOffset now, CancellationToken cancellationToken) =>
            db.MessageRatings
                .Where(r => r.MessageId == messageId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Rating, rating).SetProperty(r => r.UpdatedAt, now), cancellationToken);
    }
}
