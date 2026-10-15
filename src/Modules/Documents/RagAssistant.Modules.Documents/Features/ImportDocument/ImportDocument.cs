using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using RagAssistant.Modules.Documents.Common;
using RagAssistant.Modules.Documents.Domain;
using RagAssistant.Modules.Documents.Ingestion;
using RagAssistant.Modules.Documents.Options;
using RagAssistant.Modules.Documents.Persistence;
using RagAssistant.Modules.Documents.Persistence.Configurations;
using RagAssistant.Shared.Common;

namespace RagAssistant.Modules.Documents.Features.ImportDocument;

internal static partial class ImportDocument
{
    /// <param name="FileCount">Number of file parts in the request; exactly one is accepted.</param>
    /// <param name="EffectiveDate">Raw <c>yyyy-MM-dd</c> text, parsed by the validator so a bad value is a field error.</param>
    /// <param name="UserId">The caller's <c>sub</c> claim.</param>
    public sealed record Command(
        string? Title,
        string? OwnerName,
        string? Language,
        IReadOnlyList<string>? Tags,
        string? EffectiveDate,
        string? Source,
        int FileCount,
        UploadedFile? File,
        string UserId) : IRequest<Result<Response>>;

    public sealed record Response(Guid DocumentId, Guid VersionId, int VersionNumber, Guid IngestionRunId, string Status);

    public sealed class Validator : AbstractValidator<Command>
    {
        public Validator() =>
            this.AddMetadataRules(c => c.Title, c => c.OwnerName, c => c.Language, c => c.Tags, c => c.EffectiveDate, c => c.Source);
    }

    internal sealed partial class Handler(
        DocumentsDbContext db,
        IOptions<ImportOptions> options,
        DocumentsMetrics metrics,
        IngestionQueue queue,
        TimeProvider clock,
        ILogger<Handler> logger) : IRequestHandler<Command, Result<Response>>
    {
        public async ValueTask<Result<Response>> Handle(Command command, CancellationToken cancellationToken)
        {
            var intake = await UploadIntake.ReadAsync(command.FileCount, command.File, options.Value, cancellationToken);
            if (intake.IsFailure)
            {
                return Result.Failure<Response>(intake.Error, intake.FieldErrors!);
            }

            var accepted = intake.Value;
            var hash = accepted.Hash;
            var existingId = await FindActiveDuplicateAsync(hash, cancellationToken);
            if (existingId is not null)
            {
                return Duplicate(existingId);
            }

            // The blob goes in place before the row that references it; a failure after this leaves an unreferenced file, which is harmless.
            var storageKey = accepted.Staged is null ? null : await accepted.Staged.CommitAsync(cancellationToken);

            var now = clock.GetUtcNow();
            var document = new Document
            {
                Id = Guid.CreateVersion7(),
                Title = command.Title!.Trim(),
                OwnerName = command.OwnerName!.Trim(),
                Language = DocumentMetadata.NormalizeLanguage(command.Language),
                Tags = DocumentMetadata.NormalizeTags(command.Tags),
                Source = string.IsNullOrWhiteSpace(command.Source) ? null : command.Source.Trim(),
                Status = DocumentStatus.Active,
                LatestContentHash = hash,
                CreatedAt = now,
                CreatedBy = command.UserId,
                UpdatedAt = now,
                UpdatedBy = command.UserId,
            };
            _ = DocumentMetadata.TryParseDate(command.EffectiveDate, out var effectiveDate); // the validator already accepted it
            document.EffectiveDate = effectiveDate;

            var version = new DocumentVersion
            {
                Id = Guid.CreateVersion7(),
                DocumentId = document.Id,
                Number = 1,
                Origin = VersionOrigin.Upload,
                Status = VersionStatus.Queued,
                SourceFormat = accepted.Format,
                OriginalFileName = accepted.FileName,
                ContentType = accepted.ContentType,
                Content = accepted.Content,
                StorageKey = storageKey,
                ContentHash = hash,
                SizeBytes = accepted.SizeBytes,
                CreatedAt = now,
                CreatedBy = command.UserId,
            };
            var run = new IngestionRun
            {
                Id = Guid.CreateVersion7(),
                VersionId = version.Id,
                Attempt = 1,
                Status = RunStatus.Queued,
                CurrentStage = IngestionStage.Queued,
                QueuedAt = now,
            };
            run.Events.Add(new IngestionEvent
            {
                Stage = IngestionStage.Queued,
                Status = EventStatus.Completed,
                Message = "Import accepted.",
                OccurredAt = now,
            });
            version.Runs.Add(run);
            document.Versions.Add(version);
            db.Documents.Add(document);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: DocumentConfiguration.ActiveContentHashIndex,
            })
            {
                // A concurrent import of the same content won the race between the check above and this insert.
                db.ChangeTracker.Clear();
                return Duplicate(await FindActiveDuplicateAsync(hash, cancellationToken));
            }

            // After commit: if the process dies before this line the run is still Queued in the table and startup recovery picks it up.
            queue.Enqueue(run.Id);
            metrics.Imported(accepted.Format);
            LogImported(logger, document.Id, version.Id, accepted.SizeBytes, accepted.Format);
            return new Response(document.Id, version.Id, version.Number, run.Id, "queued");
        }

        private Task<Guid?> FindActiveDuplicateAsync(string hash, CancellationToken cancellationToken) =>
            db.Documents
                .Where(d => d.Status == DocumentStatus.Active && d.LatestContentHash == hash)
                .Select(d => (Guid?)d.Id)
                .FirstOrDefaultAsync(cancellationToken);

        private static Result<Response> Duplicate(Guid? existingId) =>
            Error.Conflict(
                "documents.duplicate_content",
                "A document with identical content already exists.",
                existingId is null ? null : new Dictionary<string, object?> { ["existingDocumentId"] = existingId });

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Imported document {DocumentId} version {VersionId} ({SizeBytes} bytes, {Format}); ingestion queued")]
        private static partial void LogImported(ILogger logger, Guid documentId, Guid versionId, int sizeBytes, SourceFormat format);
    }
}
