using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RagAssistant.Modules.Documents.Common;
using RagAssistant.Modules.Documents.Persistence;
using RagAssistant.SharedKernel.Common;

namespace RagAssistant.Modules.Documents.Features.UpdateDocument;

internal static partial class UpdateDocument
{
    /// <param name="ExpectedRevision">The revision from <c>If-Match</c>; null when the header is missing or not a strong ETag.</param>
    /// <param name="EffectiveDate">Raw <c>yyyy-MM-dd</c> text, parsed by the validator so a bad value is a field error.</param>
    /// <param name="UserId">The caller's <c>sub</c> claim.</param>
    public sealed record Command(
        Guid DocumentId,
        int? ExpectedRevision,
        string? Title,
        string? OwnerName,
        string? Language,
        IReadOnlyList<string>? Tags,
        string? EffectiveDate,
        string? Source,
        string UserId) : IRequest<Result<Guid>>;

    public sealed class Validator : AbstractValidator<Command>
    {
        public Validator() =>
            this.AddMetadataRules(c => c.Title, c => c.OwnerName, c => c.Language, c => c.Tags, c => c.EffectiveDate, c => c.Source);
    }

    internal sealed partial class Handler(
        DocumentsDbContext db,
        DocumentsMetrics metrics,
        TimeProvider clock,
        ILogger<Handler> logger) : IRequestHandler<Command, Result<Guid>>
    {
        public async ValueTask<Result<Guid>> Handle(Command command, CancellationToken cancellationToken)
        {
            if (command.ExpectedRevision is not { } expected)
            {
                return DocumentErrors.PreconditionRequired;
            }

            var document = await db.Documents.FirstOrDefaultAsync(d => d.Id == command.DocumentId, cancellationToken);
            if (document is null)
            {
                return DocumentErrors.NotFound;
            }

            if (document.Revision != expected)
            {
                return DocumentErrors.EditConflict(document.Revision);
            }

            document.Title = command.Title!.Trim();
            document.OwnerName = command.OwnerName!.Trim();
            document.Language = DocumentMetadata.NormalizeLanguage(command.Language);
            document.Tags = DocumentMetadata.NormalizeTags(command.Tags);
            document.Source = string.IsNullOrWhiteSpace(command.Source) ? null : command.Source.Trim();
            _ = DocumentMetadata.TryParseDate(command.EffectiveDate, out var effectiveDate); // the validator already accepted it
            document.EffectiveDate = effectiveDate;
            document.UpdatedAt = clock.GetUtcNow();
            document.UpdatedBy = command.UserId;

            // The UPDATE is conditional on the revision read above, so a concurrent edit that got in first fails this one.
            db.Entry(document).Property(d => d.Revision).OriginalValue = expected;
            document.Revision = expected + 1;

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                db.ChangeTracker.Clear();
                var current = await db.Documents.AsNoTracking()
                    .Where(d => d.Id == command.DocumentId)
                    .Select(d => (int?)d.Revision)
                    .FirstOrDefaultAsync(cancellationToken);
                return DocumentErrors.EditConflict(current);
            }

            metrics.MetadataUpdated();
            LogUpdated(logger, document.Id, document.Revision);
            return document.Id;
        }

        [LoggerMessage(Level = LogLevel.Information, Message = "Updated metadata of document {DocumentId} to revision {Revision}")]
        private static partial void LogUpdated(ILogger logger, Guid documentId, int revision);
    }
}
