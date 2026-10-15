using Mediator;
using Microsoft.EntityFrameworkCore;
using RagAssistant.Modules.Documents.Persistence;
using RagAssistant.SharedKernel.Common;

namespace RagAssistant.Modules.Documents.Features.GetFacets;

internal static class GetFacets
{
    public sealed record Query : IRequest<Result<Response>>;

    public sealed record FacetValue(string Value, int Count);

    public sealed record Response(FacetValue[] Owners, FacetValue[] Languages, FacetValue[] Tags);

    internal sealed class Handler(DocumentsDbContext db) : IRequestHandler<Query, Result<Response>>
    {
        public async ValueTask<Result<Response>> Handle(Query query, CancellationToken cancellationToken)
        {
            var owners = await db.Documents.AsNoTracking()
                .GroupBy(d => d.OwnerName)
                .Select(g => new FacetValue(g.Key, g.Count()))
                .ToListAsync(cancellationToken);

            var languages = await db.Documents.AsNoTracking()
                .GroupBy(d => d.Language)
                .Select(g => new FacetValue(g.Key, g.Count()))
                .ToListAsync(cancellationToken);

            // Tag arrays are tiny (ADR 0004 volume), so counting them in memory keeps the query trivially portable.
            var tagArrays = await db.Documents.AsNoTracking().Select(d => d.Tags).ToListAsync(cancellationToken);
            var tags = tagArrays
                .SelectMany(t => t.Distinct(StringComparer.Ordinal))
                .GroupBy(t => t, StringComparer.Ordinal)
                .Select(g => new FacetValue(g.Key, g.Count()));

            return new Response(Order(owners), Order(languages), Order(tags));
        }

        private static FacetValue[] Order(IEnumerable<FacetValue> values) =>
            [.. values.OrderByDescending(v => v.Count).ThenBy(v => v.Value, StringComparer.Ordinal)];
    }
}
