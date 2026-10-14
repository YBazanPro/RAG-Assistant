namespace RagAssistant.Modules.Documents.Ingestion;

internal static class IngestionErrorCodes
{
    public const string EmbeddingUnavailable = "documents.embedding_unavailable";
    public const string EmbeddingDimensionMismatch = "documents.embedding_dimension_mismatch";
    public const string NoPassages = "documents.no_passages";
    public const string IngestionError = "documents.ingestion_error";
}

/// <summary>An expected ingestion failure: the run ends <c>Failed</c> with this code and a content-free message.</summary>
internal sealed class IngestionException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
