using System.Diagnostics.Metrics;
using RagAssistant.Modules.Documents.Domain;

namespace RagAssistant.Modules.Documents.Common;

internal sealed class DocumentsMetrics
{
    public const string MeterName = "RagAssistant.Documents";
    public const string ImportedName = "documents.imported";
    public const string RejectedName = "documents.import.rejected";

    private readonly Counter<long> _imported;
    private readonly Counter<long> _rejected;

    public DocumentsMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _imported = meter.CreateCounter<long>(ImportedName, description: "Documents accepted for ingestion.");
        _rejected = meter.CreateCounter<long>(RejectedName, description: "Imports rejected, by error code.");
    }

    public void Imported(SourceFormat format) =>
        _imported.Add(1, new KeyValuePair<string, object?>("format", format.ToString().ToLowerInvariant()));

    public void Rejected(string reason) =>
        _rejected.Add(1, new KeyValuePair<string, object?>("reason", reason));
}
