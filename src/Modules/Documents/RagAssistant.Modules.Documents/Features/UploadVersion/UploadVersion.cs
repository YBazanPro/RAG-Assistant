using Mediator;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RagAssistant.Modules.Documents.Common;
using RagAssistant.Modules.Documents.Domain;
using RagAssistant.Modules.Documents.Ingestion;
using RagAssistant.Modules.Documents.Options;
using RagAssistant.Modules.Documents.Persistence;
using RagAssistant.Shared.Common;

namespace RagAssistant.Modules.Documents.Features.UploadVersion;

internal static partial class UploadVersion
{
    public sealed record Command(Guid DocumentId, int FileCount, UploadedFile? File, string UserId) : IRequest<Result<NewVersionResponse>>;

    internal sealed partial class Handler(
        DocumentsDbContext db,
        IOptions<ImportOptions> options,
        DocumentsMetrics metrics,
        IngestionQueue queue,
        TimeProvider clock,
        ILogger<Handler> logger) : IRequestHandler<Command, Result<NewVersionResponse>>
    {
        public async ValueTask<Result<NewVersionResponse>> Handle(Command command, CancellationToken cancellationToken)
        {
            var guard = await NewVersionFlow.GuardAsync(db, command.DocumentId, cancellationToken);
            if (guard.IsFailure)
            {
                return Result.Failure<NewVersionResponse>(guard.Error);
            }

            var intake = await UploadIntake.ReadAsync(command.FileCount, command.File, options.Value.MaxFileSizeBytes, cancellationToken);
            if (intake.IsFailure)
            {
                return Result.Failure<NewVersionResponse>(intake.Error, intake.FieldErrors!);
            }

            var accepted = intake.Value;

            // Same content as the latest version of this very document is a duplicate; an older version's content is a rollback and is fine.
            if (accepted.Hash == guard.Value.LatestContentHash)
            {
                return DocumentErrors.DuplicateContent(command.DocumentId);
            }

            if (await NewVersionFlow.FindOtherActiveWithHashAsync(db, command.DocumentId, accepted.Hash, cancellationToken) is { } other)
            {
                return DocumentErrors.DuplicateContent(other);
            }

            var result = await NewVersionFlow.CommitAsync(
                db, queue, clock, command.DocumentId, command.UserId, guard.Value, VersionOrigin.Upload,
                new NewVersionFlow.Source(
                    accepted.Content, accepted.Hash, accepted.Format, accepted.FileName, accepted.ContentType, accepted.SizeBytes, accepted.Hash),
                cancellationToken);
            if (result.IsSuccess)
            {
                metrics.VersionCreated(VersionOrigin.Upload);
                LogCreated(logger, command.DocumentId, result.Value.VersionId, result.Value.VersionNumber);
            }

            return result;
        }

        [LoggerMessage(Level = LogLevel.Information, Message = "Uploaded new version of document {DocumentId}: version {VersionId} (number {Number}); ingestion queued")]
        private static partial void LogCreated(ILogger logger, Guid documentId, Guid versionId, int number);
    }
}
