using System.Buffers;
using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using RagAssistant.Modules.Documents.Common;
using RagAssistant.Modules.Documents.Domain;
using RagAssistant.Modules.Documents.Options;
using RagAssistant.Modules.Documents.Persistence;
using RagAssistant.Modules.Documents.Persistence.Configurations;
using RagAssistant.Shared.Common;

namespace RagAssistant.Modules.Documents.Features.ImportDocument;

internal static partial class ImportDocument
{
    public const int MaxTitleLength = 200;
    public const int MaxOwnerNameLength = 100;
    public const int MaxTags = 20;
    public const int MaxTagLength = 50;
    public const int MaxSourceLength = 500;

    private const int MaxFileNameLength = 255;
    private const int MaxContentTypeLength = 200;

    public sealed record UploadedFile(string FileName, string? ContentType, long Length, Stream Content);

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
        public Validator()
        {
            RuleFor(c => c.Title).Cascade(CascadeMode.Stop)
                .Must(t => !string.IsNullOrWhiteSpace(t)).WithMessage("Title is required.")
                .Must(t => t!.Trim().Length <= MaxTitleLength).WithMessage($"Title must be at most {MaxTitleLength} characters.");

            RuleFor(c => c.OwnerName).Cascade(CascadeMode.Stop)
                .Must(o => !string.IsNullOrWhiteSpace(o)).WithMessage("Owner name is required.")
                .Must(o => o!.Trim().Length <= MaxOwnerNameLength).WithMessage($"Owner name must be at most {MaxOwnerNameLength} characters.");

            RuleFor(c => c.Language)
                .Must(l => DocumentMetadata.IsValidLanguage(DocumentMetadata.NormalizeLanguage(l)))
                .WithMessage("Language must be a two-letter ISO 639-1 code such as 'en'.");

            RuleFor(c => c.Tags).Cascade(CascadeMode.Stop)
                .Must(tags => DocumentMetadata.NormalizeTags(tags).All(t => t.Length > 0))
                .WithMessage("Tags must not be empty.")
                .Must(tags => DocumentMetadata.NormalizeTags(tags).All(t => t.Length <= MaxTagLength))
                .WithMessage($"Each tag must be at most {MaxTagLength} characters.")
                .Must(tags => DocumentMetadata.NormalizeTags(tags).Length <= MaxTags)
                .WithMessage($"At most {MaxTags} tags are allowed.");

            RuleFor(c => c.EffectiveDate)
                .Must(d => DocumentMetadata.TryParseDate(d, out _))
                .WithMessage($"Effective date must use the format {DocumentMetadata.DateFormat}.");

            RuleFor(c => c.Source)
                .Must(s => s is null || s.Trim().Length <= MaxSourceLength)
                .WithMessage($"Source must be at most {MaxSourceLength} characters.");
        }
    }

    internal sealed partial class Handler(
        DocumentsDbContext db,
        IOptions<ImportOptions> options,
        DocumentsMetrics metrics,
        TimeProvider clock,
        ILogger<Handler> logger) : IRequestHandler<Command, Result<Response>>
    {
        public async ValueTask<Result<Response>> Handle(Command command, CancellationToken cancellationToken)
        {
            if (command.FileCount != 1 || command.File is null)
            {
                return RejectFile("documents.file_required", "Exactly one file is required.");
            }

            var file = command.File;
            var fileName = DocumentMetadata.SanitizeFileName(file.FileName);
            if (!DocumentMetadata.TryGetFormat(fileName, out var format))
            {
                return RejectFile("documents.unsupported_extension", "Only .md, .markdown and .txt files are supported.");
            }

            var maxBytes = options.Value.MaxFileSizeBytes;
            if (file.Length > maxBytes)
            {
                return RejectFile("documents.file_too_large", $"The file must be at most {maxBytes} bytes.");
            }

            // One byte more than the limit tells an oversize stream apart from one that is exactly at the limit.
            var buffer = ArrayPool<byte>.Shared.Rent(maxBytes + 1);
            string decoded;
            int read;
            try
            {
                read = await file.Content.ReadAtLeastAsync(buffer.AsMemory(0, maxBytes + 1), maxBytes + 1, throwOnEndOfStream: false, cancellationToken);
                if (read > maxBytes)
                {
                    return RejectFile("documents.file_too_large", $"The file must be at most {maxBytes} bytes.");
                }

                if (!DocumentText.TryDecode(buffer.AsSpan(0, read), out decoded!))
                {
                    return RejectFile("documents.invalid_utf8", "The file must be valid UTF-8 text.");
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            var content = DocumentText.Normalize(decoded);
            if (string.IsNullOrWhiteSpace(content))
            {
                return RejectFile("documents.empty_content", "The file has no content.");
            }

            var hash = DocumentText.Hash(content);
            var existingId = await FindActiveDuplicateAsync(hash, cancellationToken);
            if (existingId is not null)
            {
                return Duplicate(existingId);
            }

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
                SourceFormat = format,
                OriginalFileName = Truncate(fileName, MaxFileNameLength),
                ContentType = file.ContentType is null ? null : Truncate(file.ContentType, MaxContentTypeLength),
                Content = content,
                ContentHash = hash,
                SizeBytes = read,
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

            metrics.Imported(format);
            LogImported(logger, document.Id, version.Id, read, format);
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

        private static Result<Response> RejectFile(string code, string message) =>
            Result.Failure<Response>(
                Error.Validation(code, message),
                new Dictionary<string, string[]> { ["file"] = [message] });

        private static string Truncate(string value, int maxLength) =>
            value.Length <= maxLength ? value : value[..maxLength];

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Imported document {DocumentId} version {VersionId} ({SizeBytes} bytes, {Format}); ingestion queued")]
        private static partial void LogImported(ILogger logger, Guid documentId, Guid versionId, int sizeBytes, SourceFormat format);
    }
}
