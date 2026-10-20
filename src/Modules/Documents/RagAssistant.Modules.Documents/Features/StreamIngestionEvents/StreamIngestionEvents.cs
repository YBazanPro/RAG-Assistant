using System.Globalization;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RagAssistant.Modules.Documents.Common;
using RagAssistant.Modules.Documents.Domain;
using RagAssistant.Modules.Documents.Options;
using RagAssistant.Modules.Documents.Persistence;
using RagAssistant.SharedKernel.Common;

namespace RagAssistant.Modules.Documents.Features.StreamIngestionEvents;

/// <summary>Tails the persisted stage log of a document's latest run as Server-Sent Events.</summary>
internal static class StreamIngestionEvents
{
    public const string StageEventType = "ingestion.stage";
    public const string HeartbeatEventType = "heartbeat";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed record Target(Guid RunId, Guid VersionId, int VersionNumber, int Attempt);

    public sealed record StageEvent(
        Guid RunId,
        Guid VersionId,
        int VersionNumber,
        int Attempt,
        string Stage,
        string Status,
        string? Message,
        DateTimeOffset OccurredAt,
        int? Current,
        int? Total);

    /// <summary>The latest run of the latest version; the 404 has to be decided before the response becomes a stream.</summary>
    public static async Task<Result<Target>> ResolveAsync(DocumentsDbContext db, Guid documentId, string viewerSub, CancellationToken cancellationToken)
    {
        // An unreadable document has no stream, exactly like an unknown id.
        if (!await db.Documents.AsNoTracking().ReadableBy(viewerSub).AnyAsync(d => d.Id == documentId, cancellationToken))
        {
            return DocumentErrors.NotFound;
        }

        var version = await db.Versions.AsNoTracking()
            .Where(v => v.DocumentId == documentId)
            .OrderByDescending(v => v.Number)
            .Select(v => new { v.Id, v.Number })
            .FirstOrDefaultAsync(cancellationToken);
        if (version is null)
        {
            return DocumentErrors.NotFound;
        }

        var run = await db.IngestionRuns.AsNoTracking()
            .Where(r => r.VersionId == version.Id)
            .OrderByDescending(r => r.Attempt)
            .Select(r => new { r.Id, r.Attempt })
            .FirstOrDefaultAsync(cancellationToken);
        return run is null ? DocumentErrors.NotFound : new Target(run.Id, version.Id, version.Number, run.Attempt);
    }

    /// <summary>A missing or non-numeric header means "from the start of the run".</summary>
    public static long ParseLastEventId(string? header) =>
        long.TryParse(header, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0;

    public static async IAsyncEnumerable<SseItem<string>> ReadAsync(
        DocumentsDbContext db,
        IngestionOptions options,
        TimeProvider clock,
        Target target,
        long lastEventId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var lastSent = clock.GetTimestamp();
        while (true)
        {
            // Status first, events second: a terminal status is committed with its last event, so nothing can be missed. The
            // suggestion step (spec 020) commits its outcome with its event the same way, after the run has succeeded.
            var status = await db.IngestionRuns.AsNoTracking()
                .Where(r => r.Id == target.RunId)
                .Select(r => new { r.Status, Suggestions = db.Versions.Where(v => v.Id == target.VersionId).Select(v => v.SuggestionsStatus).First() })
                .SingleAsync(cancellationToken);
            var events = await db.IngestionEvents.AsNoTracking()
                .Where(e => e.RunId == target.RunId && e.Id > lastEventId)
                .OrderBy(e => e.Id)
                .ToListAsync(cancellationToken);
            var suggestionsPending = status.Suggestions == SuggestionsStatus.Pending;

            foreach (var e in events)
            {
                lastEventId = e.Id;
                lastSent = clock.GetTimestamp();
                var data = new StageEvent(
                    target.RunId, target.VersionId, target.VersionNumber, target.Attempt,
                    e.Stage.ToApi(), e.Status.ToApi(), e.Message, e.OccurredAt, e.Current, e.Total);
                yield return new SseItem<string>(JsonSerializer.Serialize(data, Json), StageEventType)
                {
                    EventId = e.Id.ToString(CultureInfo.InvariantCulture),
                };

                // After "completed" the stream stays open only while suggested questions are being generated.
                if (e.Status == EventStatus.Failed
                    || e.Stage == IngestionStage.Suggestions
                    || (e.Stage == IngestionStage.Completed && e.Status == EventStatus.Completed && !suggestionsPending))
                {
                    yield break;
                }
            }

            if (events.Count == 0 && status.Status is (RunStatus.Succeeded or RunStatus.Failed) && !suggestionsPending)
            {
                yield break;
            }

            if (clock.GetElapsedTime(lastSent) >= options.HeartbeatInterval)
            {
                lastSent = clock.GetTimestamp();
                yield return new SseItem<string>(string.Empty, HeartbeatEventType);
            }

            await Task.Delay(options.ProgressPollInterval, clock, cancellationToken);
        }
    }
}
