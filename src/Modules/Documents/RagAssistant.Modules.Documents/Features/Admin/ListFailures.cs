using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using RagAssistant.Modules.Documents.Domain;
using RagAssistant.Modules.Documents.Persistence;
using RagAssistant.SharedKernel.Common;

namespace RagAssistant.Modules.Documents.Features.Admin;

/// <summary>
/// Spec 017: documents whose latest version failed to index, newest failure first. It deliberately does not use <c>ReadableBy</c>:
/// a maintainer sees a private document's failure as metadata (title, owner, error), but cannot retry it (ADR 0009).
/// </summary>
internal static class ListFailures
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;

    public sealed record Query(string? Page, string? PageSize) : IRequest<Result<Page<FailureItem>>>;

    public sealed class Validator : AbstractValidator<Query>
    {
        public Validator() => this.AddPagingRules(q => q.Page, q => q.PageSize, MaxPageSize);
    }

    internal sealed class Handler(DocumentsDbContext db) : IRequestHandler<Query, Result<Page<FailureItem>>>
    {
        public async ValueTask<Result<Page<FailureItem>>> Handle(Query query, CancellationToken cancellationToken)
        {
            var page = PagingRules.ParsePage(query.Page);
            var pageSize = PagingRules.ParsePageSize(query.PageSize, DefaultPageSize);

            var failed = from d in db.Documents.AsNoTracking()
                         let latest = d.Versions.OrderByDescending(v => v.Number).FirstOrDefault()
                         where latest != null && latest.Status == VersionStatus.Failed
                         select new
                         {
                             d.Id,
                             d.Title,
                             d.Visibility,
                             d.Status,
                             d.CreatedBy,
                             VersionId = latest.Id,
                             latest.Number,
                             FailedAt = latest.Runs.Max(r => r.FinishedAt),
                         };

            var total = await failed.CountAsync(cancellationToken);
            var rows = await failed
                .OrderByDescending(f => f.FailedAt).ThenBy(f => f.Id)
                .Skip(PagingRules.Skip(page, pageSize))
                .Take(pageSize)
                .ToListAsync(cancellationToken);

            var versionIds = rows.Select(r => r.VersionId).ToArray();
            var runs = (await db.IngestionRuns.AsNoTracking()
                    .Where(r => versionIds.Contains(r.VersionId))
                    .Select(r => new { r.VersionId, r.Attempt, r.CurrentStage, r.ErrorCode, r.ErrorMessage })
                    .ToListAsync(cancellationToken))
                .GroupBy(r => r.VersionId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.Attempt).First());

            var items = rows
                .Select(r =>
                {
                    runs.TryGetValue(r.VersionId, out var run);
                    return new FailureItem(
                        r.Id,
                        r.VersionId,
                        r.Number,
                        r.Title,
                        r.Visibility == Visibility.Private ? "private" : "shared",
                        r.CreatedBy,
                        run?.CurrentStage.ToString().ToLowerInvariant(),
                        run?.ErrorCode,
                        run?.ErrorMessage,
                        r.FailedAt,
                        run?.Attempt ?? 0,
                        // Mirrors RetryIngestion: an active document the caller can read; a maintainer reads shared ones only.
                        r.Visibility == Visibility.Shared && r.Status == DocumentStatus.Active);
                })
                .ToList();
            return new Page<FailureItem>(items, page, pageSize, total);
        }
    }
}
