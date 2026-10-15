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
    public const string VersionsCreatedName = "documents.versions.created";
    public const string MetadataUpdatedName = "documents.metadata.updated";
    public const string ArchivedName = "documents.archived";
    public const string RestoredName = "documents.restored";
    public const string LifecycleRejectedName = "documents.lifecycle.rejected";
    public const string IngestionDurationName = "documents.ingestion.duration";
    public const string PdfPagesExtractedName = "documents.pdf.pages.extracted";
    public const string PdfExtractionDurationName = "documents.pdf.extraction.duration";

    private readonly Counter<long> _imported;
    private readonly Counter<long> _rejected;
    private readonly Counter<long> _ingestionCompleted;
    private readonly Counter<long> _ingestionFailed;
    private readonly Counter<long> _passagesCreated;
    private readonly Counter<long> _versionsCreated;
    private readonly Counter<long> _metadataUpdated;
    private readonly Counter<long> _archived;
    private readonly Counter<long> _restored;
    private readonly Counter<long> _lifecycleRejected;
    private readonly Histogram<double> _ingestionDuration;
    private readonly Counter<long> _pdfPagesExtracted;
    private readonly Histogram<double> _pdfExtractionDuration;

    public DocumentsMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _imported = meter.CreateCounter<long>(ImportedName, description: "Documents accepted for ingestion.");
        _rejected = meter.CreateCounter<long>(RejectedName, description: "Imports rejected, by error code.");
        _ingestionCompleted = meter.CreateCounter<long>(IngestionCompletedName, description: "Ingestion runs that indexed a version.");
        _ingestionFailed = meter.CreateCounter<long>(IngestionFailedName, description: "Ingestion runs that failed, by error code.");
        _passagesCreated = meter.CreateCounter<long>(PassagesCreatedName, description: "Passages stored by successful ingestion runs.");
        _versionsCreated = meter.CreateCounter<long>(VersionsCreatedName, description: "New versions created by a lifecycle action, by origin.");
        _metadataUpdated = meter.CreateCounter<long>(MetadataUpdatedName, description: "Metadata edits applied.");
        _archived = meter.CreateCounter<long>(ArchivedName, description: "Documents archived.");
        _restored = meter.CreateCounter<long>(RestoredName, description: "Documents restored.");
        _lifecycleRejected = meter.CreateCounter<long>(LifecycleRejectedName, description: "Lifecycle actions rejected, by error code.");
        _ingestionDuration = meter.CreateHistogram<double>(IngestionDurationName, unit: "s", description: "Run start to finish.");
        _pdfPagesExtracted = meter.CreateCounter<long>(PdfPagesExtractedName, description: "Pages read from PDFs by successful extractions.");
        _pdfExtractionDuration = meter.CreateHistogram<double>(PdfExtractionDurationName, unit: "s", description: "PDF text extraction, by outcome.");
    }

    public void Imported(SourceFormat format) =>
        _imported.Add(1, new KeyValuePair<string, object?>("format", format.ToString().ToLowerInvariant()));

    public void Rejected(string reason) =>
        _rejected.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public void VersionCreated(VersionOrigin origin) =>
        _versionsCreated.Add(1, new KeyValuePair<string, object?>("origin", origin.ToString().ToLowerInvariant()));

    public void MetadataUpdated() => _metadataUpdated.Add(1);

    public void DocumentArchived() => _archived.Add(1);

    public void DocumentRestored() => _restored.Add(1);

    public void LifecycleRejected(string reason) =>
        _lifecycleRejected.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public void IngestionCompleted(int passages, TimeSpan duration)
    {
        _ingestionCompleted.Add(1);
        _passagesCreated.Add(passages);
        _ingestionDuration.Record(duration.TotalSeconds, new KeyValuePair<string, object?>("outcome", "completed"));
    }

    public void PdfExtracted(int pages, TimeSpan duration)
    {
        _pdfPagesExtracted.Add(pages);
        _pdfExtractionDuration.Record(duration.TotalSeconds, new KeyValuePair<string, object?>("outcome", "completed"));
    }

    public void PdfExtractionFailed(TimeSpan duration) =>
        _pdfExtractionDuration.Record(duration.TotalSeconds, new KeyValuePair<string, object?>("outcome", "failed"));

    public void IngestionFailed(string reason, TimeSpan duration)
    {
        _ingestionFailed.Add(1, new KeyValuePair<string, object?>("reason", reason));
        _ingestionDuration.Record(duration.TotalSeconds, new KeyValuePair<string, object?>("outcome", "failed"));
    }
}
