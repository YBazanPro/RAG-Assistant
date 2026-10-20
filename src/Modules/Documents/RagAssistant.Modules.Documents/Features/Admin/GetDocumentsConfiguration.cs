using Mediator;
using Microsoft.Extensions.Options;
using RagAssistant.Modules.Documents.Options;
using RagAssistant.SharedKernel.Common;
using RagAssistant.SharedKernel.Contracts.Retrieval;

namespace RagAssistant.Modules.Documents.Features.Admin;

/// <summary>
/// Spec 017: the effective, non-secret Documents options. The storage path is not shown, only that a file store is used. Spec 019
/// adds the hybrid and rerank stages; reranking enabled with no reranker model means it is switched on but no reranker is configured.
/// </summary>
internal static class GetDocumentsConfiguration
{
    public sealed record Query : IRequest<Result<DocumentsConfiguration>>;

    internal sealed class Handler(
        IOptions<EmbeddingOptions> embeddings,
        IOptions<ChunkingOptions> chunking,
        IOptions<IngestionOptions> ingestion,
        IOptions<RetrievalOptions> retrieval,
        IOptions<ImportOptions> import,
        IOptions<PdfOptions> pdf,
        IOptions<DocumentsAdminOptions> admin,
        IPassageReranker reranker) : IRequestHandler<Query, Result<DocumentsConfiguration>>
    {
        public ValueTask<Result<DocumentsConfiguration>> Handle(Query query, CancellationToken cancellationToken) =>
            ValueTask.FromResult<Result<DocumentsConfiguration>>(new DocumentsConfiguration(
                embeddings.Value.Model,
                embeddings.Value.Dimensions,
                chunking.Value.MaxTokens,
                chunking.Value.OverlapTokens,
                chunking.Value.MinTokens,
                ingestion.Value.MaxConcurrentRuns,
                ingestion.Value.EmbeddingBatchSize,
                ingestion.Value.EmbeddingTimeout.TotalSeconds,
                ingestion.Value.EmbeddingMaxRetries,
                retrieval.Value.DefaultTopK,
                retrieval.Value.MaxTopK,
                retrieval.Value.MinScore,
                retrieval.Value.ConfidentScore,
                retrieval.Value.DuplicateOverlap,
                import.Value.MaxFileSizeBytes,
                import.Value.MaxPdfSizeBytes,
                pdf.Value.MaxPages,
                "file system",
                admin.Value.StaleAfterDays,
                new RetrievalStagesConfiguration(
                    retrieval.Value.Hybrid.Enabled,
                    retrieval.Value.Hybrid.RrfK,
                    retrieval.Value.Reranking.Enabled,
                    reranker.ModelId,
                    retrieval.Value.Reranking.Candidates,
                    retrieval.Value.Reranking.MinScore,
                    retrieval.Value.Reranking.ConfidentScore,
                    retrieval.Value.Reranking.Timeout.TotalSeconds)));
    }
}
