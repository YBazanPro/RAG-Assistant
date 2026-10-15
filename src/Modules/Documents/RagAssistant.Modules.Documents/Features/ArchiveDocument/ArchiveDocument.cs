using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RagAssistant.Modules.Documents.Common;
using RagAssistant.Modules.Documents.Domain;
using RagAssistant.Modules.Documents.Persistence;
using RagAssistant.SharedKernel.Common;

namespace RagAssistant.Modules.Documents.Features.ArchiveDocument;

internal static partial class ArchiveDocument
{
    public sealed record Command(Guid DocumentId, string UserId) : IRequest<Result<Guid>>;

    internal sealed partial class Handler(
        DocumentsDbContext db, DocumentsMetrics metrics, TimeProvider clock, ILogger<Handler> logger) : IRequestHandler<Command, Result<Guid>>
    {
        public async ValueTask<Result<Guid>> Handle(Command command, CancellationToken cancellationToken)
        {
            var now = clock.GetUtcNow();

            // Conditional on Status only, so a running ingestion (which writes ActiveVersionId) and a metadata edit (Revision) are untouched.
            var changed = await db.Documents
                .Where(d => d.Id == command.DocumentId && d.Status == DocumentStatus.Active)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(d => d.Status, DocumentStatus.Archived)
                        .SetProperty(d => d.UpdatedAt, now)
                        .SetProperty(d => d.UpdatedBy, command.UserId),
                    cancellationToken);
            if (changed == 0)
            {
                return await db.Documents.AnyAsync(d => d.Id == command.DocumentId, cancellationToken)
                    ? DocumentErrors.AlreadyArchived
                    : DocumentErrors.NotFound;
            }

            metrics.DocumentArchived();
            LogArchived(logger, command.DocumentId);
            return command.DocumentId;
        }

        [LoggerMessage(Level = LogLevel.Information, Message = "Archived document {DocumentId}")]
        private static partial void LogArchived(ILogger logger, Guid documentId);
    }
}
