using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RagAssistant.Modules.Documents.Common;
using RagAssistant.Modules.Documents.Domain;
using RagAssistant.Modules.Documents.Persistence;
using RagAssistant.Modules.Documents.Suggestions;
using RagAssistant.SharedKernel.Common;

namespace RagAssistant.Modules.Documents.Features.EditSuggestions;

/// <summary>The owner replaces or clears the suggested questions of the active version (spec 020), guarded by the metadata ETag.</summary>
internal static partial class EditSuggestions
{
    /// <param name="ExpectedRevision">The revision from <c>If-Match</c>; null when the header is missing or not a strong ETag.</param>
    public sealed record Command(Guid DocumentId, int? ExpectedRevision, IReadOnlyList<string?>? Questions, string UserId) : IRequest<Result<Guid>>;

    public sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.Questions)
                .NotNull().WithMessage("Questions are required; send an empty list to clear them.")
                .Must(q => q is null || q.Count <= SuggestionRules.MaxQuestions)
                .WithMessage($"At most {SuggestionRules.MaxQuestions} questions.");
            RuleForEach(c => c.Questions)
                .Must(q => (q ?? "").Trim().Length <= SuggestionRules.MaxLength)
                .WithMessage($"Each question is at most {SuggestionRules.MaxLength} characters.")
                .OverridePropertyName("questions");
        }
    }

    internal sealed partial class Handler(DocumentsDbContext db, TimeProvider clock, ILogger<Handler> logger) : IRequestHandler<Command, Result<Guid>>
    {
        public async ValueTask<Result<Guid>> Handle(Command command, CancellationToken cancellationToken)
        {
            if (command.ExpectedRevision is not { } expected)
            {
                return DocumentErrors.PreconditionRequired;
            }

            // Only the owner edits them (spec 020); to anyone else the document is not there, as for its other owner-only data.
            var document = await db.Documents.AsNoTracking()
                .ReadableBy(command.UserId)
                .Where(d => d.Id == command.DocumentId && d.CreatedBy == command.UserId)
                .Select(d => new { d.Revision, d.ActiveVersionId })
                .FirstOrDefaultAsync(cancellationToken);
            if (document is null)
            {
                return DocumentErrors.NotFound;
            }

            if (document.Revision != expected)
            {
                return DocumentErrors.EditConflict(document.Revision);
            }

            if (document.ActiveVersionId is not { } activeVersionId)
            {
                return DocumentErrors.NotIndexed;
            }

            string[] questions = [.. SuggestionRules.Normalize(command.Questions!)];
            var now = clock.GetUtcNow();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            // Conditional on the revision read above, so a concurrent edit that got in first fails this one.
            var bumped = await db.Documents
                .Where(d => d.Id == command.DocumentId && d.Revision == expected)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(d => d.Revision, expected + 1).SetProperty(d => d.UpdatedAt, now).SetProperty(d => d.UpdatedBy, command.UserId),
                    cancellationToken);
            if (bumped == 0)
            {
                var current = await db.Documents.AsNoTracking()
                    .Where(d => d.Id == command.DocumentId)
                    .Select(d => (int?)d.Revision)
                    .FirstOrDefaultAsync(cancellationToken);
                return DocumentErrors.EditConflict(current);
            }

            await db.Versions.Where(v => v.Id == activeVersionId)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(v => v.SuggestedQuestions, questions)
                        .SetProperty(v => v.SuggestionsStatus, SuggestionsStatus.Edited)
                        .SetProperty(v => v.SuggestionsError, (string?)null),
                    cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            LogEdited(logger, command.DocumentId, questions.Length);
            return command.DocumentId;
        }

        [LoggerMessage(Level = LogLevel.Information, Message = "The owner set {Count} suggested questions on document {DocumentId}")]
        private static partial void LogEdited(ILogger logger, Guid documentId, int count);
    }
}
