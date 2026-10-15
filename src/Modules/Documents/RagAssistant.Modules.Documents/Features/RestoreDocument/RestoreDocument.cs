using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RagAssistant.Modules.Documents.Common;
using RagAssistant.Modules.Documents.Domain;
using RagAssistant.Modules.Documents.Persistence;
using RagAssistant.Shared.Common;

namespace RagAssistant.Modules.Documents.Features.RestoreDocument;

internal static partial class RestoreDocument
{
    public sealed record Command(Guid DocumentId, string UserId) : IRequest<Result<Guid>>;

    internal sealed partial class Handler(
        DocumentsDbContext db, DocumentsMetrics metrics, TimeProvider clock, ILogger<Handler> logger) : IRequestHandler<Command, Result<Guid>>
    {
        public async ValueTask<Result<Guid>> Handle(Command command, CancellationToken cancellationToken)
        {
            var now = clock.GetUtcNow();
            int changed;
            try
            {
                changed = await db.Documents
                    .Where(d => d.Id == command.DocumentId && d.Status == DocumentStatus.Archived)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(d => d.Status, DocumentStatus.Active)
                            .SetProperty(d => d.UpdatedAt, now)
                            .SetProperty(d => d.UpdatedBy, command.UserId),
                        cancellationToken);
            }
            catch (Exception ex) when (UniqueViolations.IsActiveContentHash(ex))
            {
                // Another active document has this content: the partial unique index is the guard (spec 009).
                var hash = await db.Documents.Where(d => d.Id == command.DocumentId).Select(d => d.LatestContentHash).SingleAsync(cancellationToken);
                var existing = await db.Documents
                    .Where(d => d.Status == DocumentStatus.Active && d.LatestContentHash == hash)
                    .Select(d => (Guid?)d.Id)
                    .FirstOrDefaultAsync(cancellationToken);
                return DocumentErrors.DuplicateContent(existing);
            }

            if (changed == 0)
            {
                return await db.Documents.AnyAsync(d => d.Id == command.DocumentId, cancellationToken)
                    ? DocumentErrors.NotArchived
                    : DocumentErrors.NotFound;
            }

            metrics.DocumentRestored();
            LogRestored(logger, command.DocumentId);
            return command.DocumentId;
        }

        [LoggerMessage(Level = LogLevel.Information, Message = "Restored document {DocumentId}")]
        private static partial void LogRestored(ILogger logger, Guid documentId);
    }
}
