using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RagAssistant.Modules.Documents.Domain;
using RagAssistant.Modules.Documents.Ingestion;
using RagAssistant.Modules.Documents.Options;
using RagAssistant.Modules.Documents.Persistence;
using RagAssistant.SharedKernel.Common;

namespace RagAssistant.Modules.Documents.Features.Admin;

/// <summary>Spec 017: counts per status, queue age and stale documents. Aggregates only, so no private document is identifiable.</summary>
internal static class GetHealth
{
    public sealed record Query : IRequest<Result<DocumentsHealth>>;

    internal sealed class Handler(DocumentsDbContext db, IOptions<DocumentsAdminOptions> options, TimeProvider clock)
        : IRequestHandler<Query, Result<DocumentsHealth>>
    {
        public async ValueTask<Result<DocumentsHealth>> Handle(Query query, CancellationToken cancellationToken)
        {
            var now = clock.GetUtcNow();
            var staleAfterDays = options.Value.StaleAfterDays;
            var cutoff = now - TimeSpan.FromDays(staleAfterDays);

            var documents = await db.Documents.AsNoTracking()
                .GroupBy(d => new { d.Status, d.Visibility })
                .Select(g => new { g.Key.Status, g.Key.Visibility, Count = g.Count() })
                .ToListAsync(cancellationToken);
            int Documents(DocumentStatus status, Visibility visibility) =>
                documents.Where(d => d.Status == status && d.Visibility == visibility).Sum(d => d.Count);

            var versions = await db.Versions.AsNoTracking()
                .GroupBy(v => v.Status)
                .Select(g => new { Status = g.Key, Count = g.Count() })
                .ToListAsync(cancellationToken);
            int Versions(VersionStatus status) => versions.Where(v => v.Status == status).Sum(v => v.Count);

            var oldestPending = await db.IngestionRuns.AsNoTracking()
                .Where(r => r.Status == RunStatus.Queued || r.Status == RunStatus.Running)
                .MinAsync(r => (DateTimeOffset?)r.QueuedAt, cancellationToken);

            var stale = await db.Documents.AsNoTracking()
                .Where(d => d.Visibility == Visibility.Shared && d.Status == DocumentStatus.Active)
                .CountAsync(d => d.Versions.OrderByDescending(v => v.Number).First().CreatedAt < cutoff, cancellationToken);

            // Spec 021: active documents whose searchable version was embedded with an older input; a reprocess updates them.
            var outdated = await db.Documents.AsNoTracking()
                .Where(d => d.Status == DocumentStatus.Active)
                .Join(db.Versions, d => d.ActiveVersionId, v => (Guid?)v.Id, (d, v) => v)
                .CountAsync(v => v.EmbeddingModel != null && (v.EmbeddingInputVersion ?? 0) < EmbeddingInput.Version, cancellationToken);

            return new DocumentsHealth(
                new DocumentCounts(
                    Documents(DocumentStatus.Active, Visibility.Shared),
                    Documents(DocumentStatus.Active, Visibility.Private),
                    Documents(DocumentStatus.Archived, Visibility.Shared),
                    Documents(DocumentStatus.Archived, Visibility.Private)),
                new VersionCounts(
                    Versions(VersionStatus.Queued),
                    Versions(VersionStatus.Processing),
                    Versions(VersionStatus.Indexed),
                    Versions(VersionStatus.Failed),
                    Versions(VersionStatus.Superseded)),
                Versions(VersionStatus.Queued),
                oldestPending is { } queuedAt ? Math.Max(0, (now - queuedAt).TotalSeconds) : null,
                stale,
                staleAfterDays,
                outdated);
        }
    }
}
