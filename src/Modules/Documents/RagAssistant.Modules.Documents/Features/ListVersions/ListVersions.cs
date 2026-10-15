using Mediator;
using Microsoft.EntityFrameworkCore;
using RagAssistant.Modules.Documents.Common;
using RagAssistant.Modules.Documents.Domain;
using RagAssistant.Modules.Documents.Persistence;
using RagAssistant.Shared.Common;

namespace RagAssistant.Modules.Documents.Features.ListVersions;

internal static class ListVersions
{
    public sealed record Query(Guid DocumentId) : IRequest<Result<Response>>;

    public sealed record RunItem(
        Guid Id,
        int Attempt,
        string Status,
        string Stage,
        string? ErrorCode,
        string? ErrorMessage,
        DateTimeOffset QueuedAt,
        DateTimeOffset? StartedAt,
        DateTimeOffset? FinishedAt);

    public sealed record VersionItem(
        Guid Id,
        int Number,
        string Status,
        string Origin,
        string SourceFormat,
        string OriginalFileName,
        int SizeBytes,
        int? PassageCount,
        bool IsActive,
        DateTimeOffset CreatedAt,
        string CreatedBy,
        DateTimeOffset? IndexedAt,
        RunItem[] Runs);

    public sealed record Response(VersionItem[] Items);

    internal sealed class Handler(DocumentsDbContext db) : IRequestHandler<Query, Result<Response>>
    {
        public async ValueTask<Result<Response>> Handle(Query query, CancellationToken cancellationToken)
        {
            var document = await db.Documents.AsNoTracking()
                .Where(d => d.Id == query.DocumentId)
                .Select(d => new { d.ActiveVersionId })
                .FirstOrDefaultAsync(cancellationToken);

            if (document is null)
            {
                return DocumentErrors.NotFound;
            }

            // Content is never projected; it can be as large as the import limit.
            var rows = await db.Versions.AsNoTracking()
                .Where(v => v.DocumentId == query.DocumentId)
                .OrderByDescending(v => v.Number)
                .Select(v => new
                {
                    v.Id,
                    v.Number,
                    v.Status,
                    v.Origin,
                    v.SourceFormat,
                    v.OriginalFileName,
                    v.SizeBytes,
                    v.PassageCount,
                    v.CreatedAt,
                    v.CreatedBy,
                    v.IndexedAt,
                    Runs = v.Runs
                        .OrderByDescending(r => r.Attempt)
                        .Select(r => new RunRow(
                            r.Id, r.Attempt, r.Status, r.CurrentStage, r.ErrorCode, r.ErrorMessage, r.QueuedAt, r.StartedAt, r.FinishedAt))
                        .ToList(),
                })
                .ToListAsync(cancellationToken);

            var items = rows.Select(v => new VersionItem(
                v.Id,
                v.Number,
                v.Status.ToApi(),
                v.Origin.ToString().ToLowerInvariant(),
                v.SourceFormat.ToApi(),
                v.OriginalFileName,
                v.SizeBytes,
                v.PassageCount,
                v.Id == document.ActiveVersionId,
                v.CreatedAt,
                v.CreatedBy,
                v.IndexedAt,
                [.. v.Runs.Select(r => new RunItem(
                    r.Id, r.Attempt, r.Status.ToApi(), r.Stage.ToApi(), r.ErrorCode, r.ErrorMessage, r.QueuedAt, r.StartedAt, r.FinishedAt))]));

            return new Response([.. items]);
        }

        private sealed record RunRow(
            Guid Id, int Attempt, RunStatus Status, IngestionStage Stage, string? ErrorCode, string? ErrorMessage,
            DateTimeOffset QueuedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt);
    }
}
