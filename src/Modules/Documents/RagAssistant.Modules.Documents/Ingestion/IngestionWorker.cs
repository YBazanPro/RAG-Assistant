using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RagAssistant.Modules.Documents.Domain;
using RagAssistant.Modules.Documents.Options;
using RagAssistant.Modules.Documents.Persistence;

namespace RagAssistant.Modules.Documents.Ingestion;

/// <summary>Consumes run ids from <see cref="IngestionQueue"/>; after a restart it first re-queues whatever the run table says is unfinished.</summary>
internal sealed partial class IngestionWorker(
    IServiceScopeFactory scopes,
    IngestionQueue queue,
    IOptions<IngestionOptions> options,
    TimeProvider clock,
    ILogger<IngestionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RequeueWhenDatabaseIsReadyAsync(stoppingToken);
            await Task.WhenAll(Enumerable.Range(0, options.Value.MaxConcurrentRuns).Select(_ => ConsumeAsync(stoppingToken)));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    // The API starts after the MigrationService, but a database that is briefly unreachable must not take the whole host down.
    private async Task RequeueWhenDatabaseIsReadyAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await RequeueUnfinishedAsync(cancellationToken);
                return;
            }
            catch (DbException ex)
            {
                LogRecoveryFailed(logger, ex, options.Value.ProbeInterval.TotalSeconds);
                await Task.Delay(options.Value.ProbeInterval, clock, cancellationToken);
            }
        }
    }

    private async Task RequeueUnfinishedAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentsDbContext>();
        var now = clock.GetUtcNow();

        var interrupted = await db.IngestionRuns
            .Where(r => r.Status == RunStatus.Running)
            .Select(r => new { r.Id, r.VersionId })
            .ToListAsync(cancellationToken);
        if (interrupted.Count > 0)
        {
            var runIds = interrupted.Select(r => r.Id).ToList();
            var versionIds = interrupted.Select(r => r.VersionId).ToList();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.IngestionRuns.Where(r => runIds.Contains(r.Id))
                .ExecuteUpdateAsync(
                    s => s.SetProperty(r => r.Status, RunStatus.Queued)
                        .SetProperty(r => r.CurrentStage, IngestionStage.Queued)
                        .SetProperty(r => r.StartedAt, (DateTimeOffset?)null),
                    cancellationToken);
            await db.Versions.Where(v => versionIds.Contains(v.Id) && v.Status == VersionStatus.Processing)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.Status, VersionStatus.Queued), cancellationToken);
            db.IngestionEvents.AddRange(runIds.Select(id => new IngestionEvent
            {
                RunId = id,
                Stage = IngestionStage.Queued,
                Status = EventStatus.Started,
                Message = "Re-queued after restart",
                OccurredAt = now,
            }));
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        var queued = await db.IngestionRuns
            .Where(r => r.Status == RunStatus.Queued)
            .OrderBy(r => r.QueuedAt)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken);
        foreach (var id in queued)
        {
            queue.Enqueue(id);
        }

        LogRequeued(logger, interrupted.Count, queued.Count);
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        await foreach (var runId in queue.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                await WaitForModelAsync(cancellationToken);
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IngestionRunProcessor>().ProcessAsync(runId, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Catch-all by design: the consumer loop must survive one bad run. The processor already fails runs for
                // everything it can classify, so what reaches here is infrastructure (typically the database), and the run is
                // handed back to the queue instead of waiting for a restart.
                LogRunCrashed(logger, ex, runId);
                await RequeueCrashedRunAsync(runId, cancellationToken);
            }
        }
    }

    private async Task RequeueCrashedRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(options.Value.ProbeInterval, clock, cancellationToken);
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<DocumentsDbContext>();
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                var reset = await db.IngestionRuns.Where(r => r.Id == runId && r.Status == RunStatus.Running)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(r => r.Status, RunStatus.Queued)
                            .SetProperty(r => r.CurrentStage, IngestionStage.Queued)
                            .SetProperty(r => r.StartedAt, (DateTimeOffset?)null),
                        cancellationToken);
                if (reset > 0)
                {
                    await db.Versions.Where(v => v.Status == VersionStatus.Processing && v.Runs.Any(r => r.Id == runId))
                        .ExecuteUpdateAsync(s => s.SetProperty(v => v.Status, VersionStatus.Queued), cancellationToken);
                    db.IngestionEvents.Add(new IngestionEvent
                    {
                        RunId = runId,
                        Stage = IngestionStage.Queued,
                        Status = EventStatus.Started,
                        Message = "Re-queued after an error",
                        OccurredAt = clock.GetUtcNow(),
                    });
                    await db.SaveChangesAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
                queue.Enqueue(runId);
                return;
            }
            catch (DbException ex)
            {
                // The database is the likely reason the run crashed; keep trying until it answers.
                LogRecoveryFailed(logger, ex, options.Value.ProbeInterval.TotalSeconds);
            }
        }
    }

    private async Task WaitForModelAsync(CancellationToken cancellationToken)
    {
        var paused = false;
        while (true)
        {
            await using (var scope = scopes.CreateAsyncScope())
            {
                if (await scope.ServiceProvider.GetRequiredService<PassageEmbedder>().IsAvailableAsync(cancellationToken))
                {
                    if (paused)
                    {
                        LogResumed(logger);
                    }

                    return;
                }
            }

            if (!paused)
            {
                paused = true;
                LogPaused(logger, options.Value.ProbeInterval.TotalSeconds);
            }

            await Task.Delay(options.Value.ProbeInterval, clock, cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Ingestion worker re-queued {Interrupted} interrupted run(s); {Queued} run(s) waiting")]
    private static partial void LogRequeued(ILogger logger, int interrupted, int queued);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ingestion recovery could not read the run table; retrying in {Seconds}s")]
    private static partial void LogRecoveryFailed(ILogger logger, Exception exception, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Embedding model is not available; ingestion paused, probing every {Seconds}s")]
    private static partial void LogPaused(ILogger logger, double seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Embedding model is available; ingestion resumed")]
    private static partial void LogResumed(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Ingestion run {RunId} crashed the worker loop; it will be re-queued at the next start")]
    private static partial void LogRunCrashed(ILogger logger, Exception exception, Guid runId);
}
