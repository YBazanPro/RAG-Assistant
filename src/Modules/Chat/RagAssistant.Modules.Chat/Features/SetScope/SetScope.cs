using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using RagAssistant.Modules.Chat.Common;
using RagAssistant.Modules.Chat.Persistence;
using RagAssistant.SharedKernel.Common;
using RagAssistant.SharedKernel.Contracts.Documents;

namespace RagAssistant.Modules.Chat.Features.SetScope;

/// <summary>
/// Replaces the documents and tags a conversation searches (spec 015). The call is idempotent: the lists are normalized and
/// de-duplicated, so the same request always gives the same stored scope. Questions already asked keep the scope they used.
/// </summary>
internal static class SetScope
{
    public sealed record Command(string UserId, Guid ConversationId, Guid[]? DocumentIds, string[]? Tags) : IRequest<Result<ConversationScope>>;

    public sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(x => x.DocumentIds)
                .Must(v => v is null || v.Distinct().Count() <= ScopeRules.MaxDocuments)
                .WithMessage($"At most {ScopeRules.MaxDocuments} documents can be chosen.");

            RuleFor(x => x.Tags)
                .Must(v => ScopeRules.NormalizeTags(v).Length <= ScopeRules.MaxTags)
                .WithMessage($"At most {ScopeRules.MaxTags} tags can be combined.");
        }
    }

    internal sealed class Handler(ChatDbContext db, IDocumentCatalog catalog) : IRequestHandler<Command, Result<ConversationScope>>
    {
        public async ValueTask<Result<ConversationScope>> Handle(Command command, CancellationToken cancellationToken)
        {
            var conversation = await db.Conversations.SingleOrDefaultAsync(
                c => c.Id == command.ConversationId && c.OwnerSub == command.UserId, cancellationToken);
            if (conversation is null)
            {
                return ChatErrors.ConversationNotFound;
            }

            var documentIds = (command.DocumentIds ?? []).Distinct().ToArray();
            var availability = await catalog.CheckAsync(documentIds, command.UserId, cancellationToken);
            var unknown = documentIds.Where(id => !availability.Known.Contains(id)).ToArray();
            if (unknown.Length > 0)
            {
                return Result.Failure<ConversationScope>(
                    Error.Validation(Result.ValidationFailedCode, "One or more validation errors occurred."),
                    new Dictionary<string, string[]>
                    {
                        ["documentIds"] = [$"These documents do not exist or cannot be used: {string.Join(", ", unknown)}."],
                    });
            }

            conversation.ScopeDocumentIds = documentIds;
            conversation.ScopeTags = ScopeRules.NormalizeTags(command.Tags);
            await db.SaveChangesAsync(cancellationToken);

            return ConversationScope.From(conversation.ScopeDocumentIds, conversation.ScopeTags, availability);
        }
    }
}
