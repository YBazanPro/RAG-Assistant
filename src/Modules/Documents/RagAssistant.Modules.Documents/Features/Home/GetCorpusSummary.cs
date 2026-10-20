using Mediator;
using Microsoft.EntityFrameworkCore;
using RagAssistant.Modules.Documents.Common;
using RagAssistant.Modules.Documents.Domain;
using RagAssistant.Modules.Documents.Persistence;
using RagAssistant.SharedKernel.Common;

namespace RagAssistant.Modules.Documents.Features.Home;

/// <summary>What the home page says about the corpus (spec 020): only active documents the caller can read.</summary>
internal static class GetCorpusSummary
{
    public const int RecentCount = 5;

    public sealed record Query(string ViewerSub) : IRequest<Result<Response>>;

    public sealed record LanguageCount(string Language, int Count);

    public sealed record RecentDocument(Guid Id, string Title, DateTimeOffset UpdatedAt, string Language);

    public sealed record Response(int ActiveDocuments, IReadOnlyList<LanguageCount> ByLanguage, DateTimeOffset? LastIngestedAt, IReadOnlyList<RecentDocument> Recent);

    internal sealed class Handler(DocumentsDbContext db) : IRequestHandler<Query, Result<Response>>
    {
        public async ValueTask<Result<Response>> Handle(Query query, CancellationToken cancellationToken)
        {
            var active = db.Documents.AsNoTracking().ReadableBy(query.ViewerSub).Where(d => d.Status == DocumentStatus.Active);

            var languages = await active
                .GroupBy(d => d.Language)
                .Select(g => new LanguageCount(g.Key, g.Count()))
                .ToListAsync(cancellationToken);
            var lastIngestedAt = await active
                .SelectMany(d => d.Versions)
                .MaxAsync(v => v.IndexedAt, cancellationToken);
            var recent = await active
                .OrderByDescending(d => d.UpdatedAt)
                .ThenBy(d => d.Id)
                .Take(RecentCount)
                .Select(d => new RecentDocument(d.Id, d.Title, d.UpdatedAt, d.Language))
                .ToListAsync(cancellationToken);

            return new Response(
                languages.Sum(l => l.Count),
                [.. languages.OrderByDescending(l => l.Count).ThenBy(l => l.Language, StringComparer.Ordinal)],
                lastIngestedAt,
                recent);
        }
    }
}
