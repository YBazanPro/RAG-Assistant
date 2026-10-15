namespace RagAssistant.Modules.Documents.Ingestion;

internal static class IngestionErrorCodes
{
    public const string EmbeddingUnavailable = "documents.embedding_unavailable";
    public const string EmbeddingDimensionMismatch = "documents.embedding_dimension_mismatch";
    public const string NoPassages = "documents.no_passages";
    public const string IngestionError = "documents.ingestion_error";
    public const string RequiresOcr = "documents.requires_ocr";
    public const string TextLayerUnusable = "documents.text_layer_unusable";
    public const string PdfEncrypted = "documents.pdf_encrypted";
    public const string PdfInvalid = "documents.pdf_invalid";
    public const string PdfTooManyPages = "documents.pdf_too_many_pages";
    public const string ExtractionTimeout = "documents.extraction_timeout";
    public const string FileMissing = "documents.file_missing";
}

/// <summary>An expected ingestion failure: the run ends <c>Failed</c> with this code and a content-free message.</summary>
internal sealed class IngestionException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
