namespace RagAssistant.Modules.Documents.Domain;

internal enum DocumentStatus
{
    Active,
    Archived,
}

/// <summary>Who can read a document (ADR 0009): everyone signed in, or only the user who imported it.</summary>
internal enum Visibility
{
    Shared,
    Private,
}

/// <summary>Where a document's language comes from (spec 018): detection at ingestion, or the owner's explicit choice.</summary>
/// <remarks>Detect comes first: it is the column default, and EF skips a value equal to the CLR default on insert.</remarks>
internal enum LanguageSource
{
    Detect,
    Owner,
}

/// <summary>How sure ingestion was about a version's language (spec 018); an uncertain guess is recorded but never applied.</summary>
internal enum LanguageConfidence
{
    Confident,
    Uncertain,
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
