using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RagAssistant.Modules.Documents.Common;
using RagAssistant.Modules.Documents.Domain;
using RagAssistant.Modules.Documents.Ingestion;
using RagAssistant.Modules.Documents.Persistence;
using RagAssistant.SharedKernel.Common;

namespace RagAssistant.Modules.Documents.Features.ReprocessDocument;

internal static partial class ReprocessDocument
{
    public sealed record Command(Guid DocumentId, string UserId) : IRequest<Result<NewVersionResponse>>;

    internal sealed partial class Handler(
        DocumentsDbContext db, DocumentsMetrics metrics, IngestionQueue queue, TimeProvider clock, ILogger<Handler> logger)
        : IRequestHandler<Command, Result<NewVersionResponse>>
    {
        public async ValueTask<Result<NewVersionResponse>> Handle(Command command, CancellationToken cancellationToken)
        {
            var guard = await NewVersionFlow.GuardAsync(db, command.DocumentId, command.UserId, cancellationToken);
            if (guard.IsFailure)
            {
                return Result.Failure<NewVersionResponse>(guard.Error);
            }

            if (guard.Value.LatestStatus != VersionStatus.Indexed)
            {
                return DocumentErrors.NotReprocessable;
            }

            var latestNumber = guard.Value.LatestNumber;
            var source = await db.Versions.AsNoTracking()
                .Where(v => v.DocumentId == command.DocumentId && v.Number == latestNumber)
                .Select(v => new NewVersionFlow.Source(v.Content, v.StorageKey, v.ContentHash, v.SourceFormat, v.OriginalFileName, v.ContentType, v.SizeBytes, null))
                .SingleAsync(cancellationToken);

            var result = await NewVersionFlow.CommitAsync(
                db, queue, clock, command.DocumentId, command.UserId, guard.Value, VersionOrigin.Reprocess, source, cancellationToken);
            if (result.IsSuccess)
            {
                metrics.VersionCreated(VersionOrigin.Reprocess);
                LogCreated(logger, command.DocumentId, result.Value.VersionId, result.Value.VersionNumber);
            }

            return result;
        }

        [LoggerMessage(Level = LogLevel.Information, Message = "Reprocessing document {DocumentId}: version {VersionId} (number {Number}); ingestion queued")]
        private static partial void LogCreated(ILogger logger, Guid documentId, Guid versionId, int number);
    }
}
