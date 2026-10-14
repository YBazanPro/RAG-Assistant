using Mediator;
using Microsoft.EntityFrameworkCore;
using RagAssistant.Modules.Documents.Common;
using RagAssistant.Modules.Documents.Domain;
using RagAssistant.Modules.Documents.Ingestion;
using RagAssistant.Modules.Documents.Persistence;
using RagAssistant.Shared.Common;

namespace RagAssistant.Modules.Documents.Features.RetryIngestion;

internal static class RetryIngestion
{
    public sealed record Command(Guid DocumentId) : IRequest<Result<Response>>;

    public sealed record Response(Guid DocumentId, Guid VersionId, Guid IngestionRunId, int Attempt, string Status);

    internal sealed class Handler(DocumentsDbContext db, IngestionQueue queue, TimeProvider clock) : IRequestHandler<Command, Result<Response>>
    {
        private static readonly Error NotRetryable =
            Error.Conflict("documents.not_retryable", "Only a document whose latest version failed to index can be retried.");

        public async ValueTask<Result<Response>> Handle(Command command, CancellationToken cancellationToken)
        {
            var document = await db.Documents.AsNoTracking()
                .Where(d => d.Id == command.DocumentId)
                .Select(d => new
                {
                    d.Status,
                    Latest = d.Versions
                        .OrderByDescending(v => v.Number)
                        .Select(v => new { v.Id, v.Status, Attempt = v.Runs.Max(r => (int?)r.Attempt) ?? 0 })
                        .FirstOrDefault(),
                })
                .FirstOrDefaultAsync(cancellationToken);
            if (document is null)
            {
                return DocumentErrors.NotFound;
            }

            if (document.Status != DocumentStatus.Active || document.Latest is not { Status: VersionStatus.Failed } latest)
            {
                return NotRetryable;
            }

            var now = clock.GetUtcNow();
            var run = new IngestionRun
            {
                Id = Guid.CreateVersion7(),
                VersionId = latest.Id,
                Attempt = latest.Attempt + 1,
                Status = RunStatus.Queued,
                CurrentStage = IngestionStage.Queued,
                QueuedAt = now,
            };
            run.Events.Add(new IngestionEvent
            {
                Stage = IngestionStage.Queued,
                Status = EventStatus.Completed,
                Message = "Retry requested",
                OccurredAt = now,
            });

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            // Conditional, so two concurrent retries cannot both pass: the loser updates no row.
            var claimed = await db.Versions
                .Where(v => v.Id == latest.Id && v.Status == VersionStatus.Failed)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.Status, VersionStatus.Queued), cancellationToken);
            if (claimed == 0)
            {
                return NotRetryable;
            }

            db.IngestionRuns.Add(run);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            queue.Enqueue(run.Id);
            return new Response(command.DocumentId, latest.Id, run.Id, run.Attempt, "queued");
        }
    }
}
