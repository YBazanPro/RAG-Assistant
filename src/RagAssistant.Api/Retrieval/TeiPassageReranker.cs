using System.Net.Http.Json;
using System.Text.Json.Serialization;
using RagAssistant.SharedKernel.Contracts.Retrieval;

namespace RagAssistant.Api.Retrieval;

/// <summary>
/// The cross-encoder behind <see cref="IPassageReranker"/>: Hugging Face Text Embeddings Inference serving
/// <c>BAAI/bge-reranker-v2-m3</c> (spec 019, ADR 0010). Provider-specific, so it lives in the API; the Documents module only
/// sees the interface and turns every failure here into "reranking skipped".
/// </summary>
internal sealed class TeiPassageReranker(HttpClient http, string modelId) : IPassageReranker
{
    /// <summary>The Aspire resource name; service discovery resolves <c>http://reranker</c> to it.</summary>
    public const string ResourceName = "reranker";

    public const string ModelConfigurationKey = "Reranker:Model";

    public const string DefaultModel = "BAAI/bge-reranker-v2-m3";

    public string? ModelId => modelId;

    public async Task<IReadOnlyList<double>> RerankAsync(string query, IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        // raw_scores false: sigmoid scores in 0..1, so thresholds read the same for every query. truncate: TEI cuts a passage to
        // the model's 512 tokens instead of refusing the whole batch (ADR 0010).
        using var response = await http.PostAsJsonAsync(
            "rerank", new RerankRequest(query, texts, RawScores: false, Truncate: true), cancellationToken);
        response.EnsureSuccessStatusCode();
        var ranked = await response.Content.ReadFromJsonAsync<List<RerankScore>>(cancellationToken)
            ?? throw new InvalidOperationException("The reranker returned no scores.");

        // TEI answers best first; the contract wants the order of the texts.
        var scores = new double[texts.Count];
        var seen = new bool[texts.Count];
        foreach (var item in ranked)
        {
            if (item.Index < 0 || item.Index >= texts.Count || seen[item.Index])
            {
                throw new InvalidOperationException("The reranker returned an index outside the texts sent.");
            }

            scores[item.Index] = item.Score;
            seen[item.Index] = true;
        }

        // A missing score shortens the list, which the caller treats as a wrong count (spec 019: reranking skipped).
        return seen.All(s => s) ? scores : [.. scores.Where((_, i) => seen[i])];
    }

    private sealed record RerankRequest(
        [property: JsonPropertyName("query")] string Query,
        [property: JsonPropertyName("texts")] IReadOnlyList<string> Texts,
        [property: JsonPropertyName("raw_scores")] bool RawScores,
        [property: JsonPropertyName("truncate")] bool Truncate);

    private sealed record RerankScore(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("score")] double Score);
}
