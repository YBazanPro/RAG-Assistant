using Microsoft.EntityFrameworkCore;
using RagAssistant.Modules.Documents.Domain;
using RagAssistant.Modules.Documents.Ingestion;
using RagAssistant.Modules.Documents.Persistence;
using RagAssistant.Shared.Common;

namespace RagAssistant.Modules.Documents.Common;

public sealed record NewVersionResponse(Guid DocumentId, Guid VersionId, int VersionNumber, Guid IngestionRunId, string Status);

/// <summary>The part "upload a new version" and "reprocess" share: guards, then version + run + event in one transaction (spec 009).</summary>
internal static class NewVersionFlow
{
    internal sealed record Guard(int LatestNumber, VersionStatus LatestStatus, string LatestContentHash);

    /// <param name="NewLatestHash">The hash to store on the document, or null to leave it (reprocess).</param>
    internal sealed record Source(
        string? Content, string? StorageKey, string Hash, SourceFormat Format, string FileName, string? ContentType, int SizeBytes, string? NewLatestHash);

    public static async Task<Result<Guard>> GuardAsync(DocumentsDbContext db, Guid documentId, CancellationToken cancellationToken)
    {
        var row = await db.Documents.AsNoTracking()
            .Where(d => d.Id == documentId)
            .Select(d => new
            {
                d.Status,
                d.LatestContentHash,
                Latest = d.Versions.OrderByDescending(v => v.Number).Select(v => new { v.Number, v.Status }).FirstOrDefault(),
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return DocumentErrors.NotFound;
        }

        if (row.Status == DocumentStatus.Archived)
        {
            return DocumentErrors.Archived;
        }

        if (row.Latest is not { } latest)
        {
            return DocumentErrors.NotFound;
        }

        return latest.Status is VersionStatus.Queued or VersionStatus.Processing
            ? DocumentErrors.IngestionInProgress
            : new Guard(latest.Number, latest.Status, row.LatestContentHash);
    }

    public static async Task<Guid?> FindOtherActiveWithHashAsync(
        DocumentsDbContext db, Guid documentId, string hash, CancellationToken cancellationToken) =>
        await db.Documents.AsNoTracking()
            .Where(d => d.Id != documentId && d.Status == DocumentStatus.Active && d.LatestContentHash == hash)
            .Select(d => (Guid?)d.Id)
            .FirstOrDefaultAsync(cancellationToken);

    public static async Task<Result<NewVersionResponse>> CommitAsync(
        DocumentsDbContext db,
        IngestionQueue queue,
        TimeProvider clock,
        Guid documentId,
        string userId,
        Guard guard,
        VersionOrigin origin,
        Source source,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var version = new DocumentVersion
        {
            Id = Guid.CreateVersion7(),
            DocumentId = documentId,
            Number = guard.LatestNumber + 1,
            Origin = origin,
            Status = VersionStatus.Queued,
            SourceFormat = source.Format,
            OriginalFileName = source.FileName,
            ContentType = source.ContentType,
            Content = source.Content,
            StorageKey = source.StorageKey,
            ContentHash = source.Hash,
            SizeBytes = source.SizeBytes,
            CreatedAt = now,
            CreatedBy = userId,
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
            Message = origin == VersionOrigin.Reprocess ? "Reprocess accepted." : "New version accepted.",
            OccurredAt = now,
        });
        version.Runs.Add(run);

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            db.Versions.Add(version);
            await db.SaveChangesAsync(cancellationToken);

            var newHash = source.NewLatestHash;
            // Conditional on Active so an archive that raced in after the guard wins and this action is refused.
            var updated = await db.Documents
                .Where(d => d.Id == documentId && d.Status == DocumentStatus.Active)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(d => d.LatestContentHash, d => newHash ?? d.LatestContentHash)
                        .SetProperty(d => d.UpdatedAt, now)
                        .SetProperty(d => d.UpdatedBy, userId),
                    cancellationToken);
            if (updated == 0)
            {
                return DocumentErrors.Archived;
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception ex) when (UniqueViolations.IsVersionNumber(ex))
        {
            // A concurrent upload took the same number: only one of them can be the latest.
            db.ChangeTracker.Clear();
            return DocumentErrors.IngestionInProgress;
        }
        catch (Exception ex) when (UniqueViolations.IsActiveContentHash(ex))
        {
            db.ChangeTracker.Clear();
            return DocumentErrors.DuplicateContent(await FindOtherActiveWithHashAsync(db, documentId, source.Hash, cancellationToken));
        }

        // After commit: if the process dies before this line the run is still Queued in the table and startup recovery picks it up.
        queue.Enqueue(run.Id);
        return new NewVersionResponse(documentId, version.Id, version.Number, run.Id, "queued");
    }
}
