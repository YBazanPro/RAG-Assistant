namespace RagAssistant.Modules.Documents.Domain;

internal enum DocumentStatus
{
    Active,
    Archived,
}

internal enum VersionStatus
{
    Queued,
    Processing,
    Indexed,
    Failed,
    Superseded,
}

internal enum VersionOrigin
{
    Upload,
    Reprocess,
}

internal enum SourceFormat
{
    Markdown,
    PlainText,
    Pdf,
}

internal enum RunStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
}

internal enum IngestionStage
{
    Queued,
    Extracting,
    Chunking,
    Embedding,
    Storing,
    Completed,
}

internal enum EventStatus
{
    Started,
    Completed,
    Failed,
    Progress,
}
