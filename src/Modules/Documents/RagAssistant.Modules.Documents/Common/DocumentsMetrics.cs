using System.Diagnostics;
using System.Diagnostics.Metrics;
using RagAssistant.Modules.Documents.Domain;

namespace RagAssistant.Modules.Documents.Common;

internal static class DocumentsTelemetry
{
    public const string SourceName = "RagAssistant.Documents";

    public static readonly ActivitySource Source = new(SourceName);
}

internal sealed class DocumentsMetrics
{
    public const string MeterName = "RagAssistant.Documents";
    public const string ImportedName = "documents.imported";
    public const string RejectedName = "documents.import.rejected";
    public const string IngestionCompletedName = "documents.ingestion.completed";
    public const string IngestionFailedName = "documents.ingestion.failed";
    public const string PassagesCreatedName = "documents.passages.created";
    public const string IngestionDurationName = "documents.ingestion.duration";

    private readonly Counter<long> _imported;
    private readonly Counter<long> _rejected;
    private readonly Counter<long> _ingestionCompleted;
    private readonly Counter<long> _ingestionFailed;
    private readonly Counter<long> _passagesCreated;
    private readonly Histogram<double> _ingestionDuration;

    public DocumentsMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _imported = meter.CreateCounter<long>(ImportedName, description: "Documents accepted for ingestion.");
        _rejected = meter.CreateCounter<long>(RejectedName, description: "Imports rejected, by error code.");
        _ingestionCompleted = meter.CreateCounter<long>(IngestionCompletedName, description: "Ingestion runs that indexed a version.");
        _ingestionFailed = meter.CreateCounter<long>(IngestionFailedName, description: "Ingestion runs that failed, by error code.");
        _passagesCreated = meter.CreateCounter<long>(PassagesCreatedName, description: "Passages stored by successful ingestion runs.");
        _ingestionDuration = meter.CreateHistogram<double>(IngestionDurationName, unit: "s", description: "Run start to finish.");
    }

    public void Imported(SourceFormat format) =>
        _imported.Add(1, new KeyValuePair<string, object?>("format", format.ToString().ToLowerInvariant()));

    public void Rejected(string reason) =>
        _rejected.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public void IngestionCompleted(int passages, TimeSpan duration)
    {
        _ingestionCompleted.Add(1);
        _passagesCreated.Add(passages);
        _ingestionDuration.Record(duration.TotalSeconds, new KeyValuePair<string, object?>("outcome", "completed"));
    }

    public void IngestionFailed(string reason, TimeSpan duration)
    {
        _ingestionFailed.Add(1, new KeyValuePair<string, object?>("reason", reason));
        _ingestionDuration.Record(duration.TotalSeconds, new KeyValuePair<string, object?>("outcome", "failed"));
    }
}
