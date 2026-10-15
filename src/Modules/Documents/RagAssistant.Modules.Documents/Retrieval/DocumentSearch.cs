using System.Globalization;
using RagAssistant.Modules.Documents.Features.SearchPassages;
using RagAssistant.SharedKernel.Common;
using RagAssistant.SharedKernel.Contracts.Documents;
using ModuleMediator = RagAssistant.Modules.Documents.Generated.Mediator;

namespace RagAssistant.Modules.Documents.Retrieval;

/// <summary>The contract other modules see: the diagnostic pipeline without its diagnostics (spec 011).</summary>
internal sealed class DocumentSearch(ModuleMediator mediator) : IDocumentSearch
{
    public async Task<Result<SearchResult>> SearchAsync(
        string query, SearchFilters filters, int topK, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filters);

        var request = new SearchPassages.Query(
            query, filters.Language, [.. filters.Tags ?? []], topK.ToString(CultureInfo.InvariantCulture), IncludeDiagnostics: false);
        var run = await mediator.Send(request, cancellationToken);
        if (run.IsFailure)
        {
            // The endpoint's parameter is "q"; callers of the contract know it as "query".
            return run.FieldErrors is { } fields
                ? Result.Failure<SearchResult>(run.Error, fields.ToDictionary(f => f.Key == "q" ? "query" : f.Key, f => f.Value, StringComparer.Ordinal))
                : Result.Failure<SearchResult>(run.Error);
        }

        var value = run.Value;
        return new SearchResult(value.Outcome, value.Kept, value.Thresholds, value.EmbeddingModel);
    }
}
