using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using RagAssistant.Modules.Documents.Domain;
using RagAssistant.Modules.Documents.Persistence;
using RagAssistant.SharedKernel.Common;

namespace RagAssistant.Modules.Documents.Features.Admin;

/// <summary>
/// Spec 017, the metadata-only view of private documents that spec 016 deferred: title, owner, status and dates. It has its own
/// query and projection on purpose; no read path that serves content (list, search, detail) is widened for maintainers.
/// </summary>
internal static class ListPrivateDocuments
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;

    public sealed record Query(string? Page, string? PageSize) : IRequest<Result<Page<PrivateDocumentItem>>>;

    public sealed class Validator : AbstractValidator<Query>
    {
        public Validator() => this.AddPagingRules(q => q.Page, q => q.PageSize, MaxPageSize);
    }

    internal sealed class Handler(DocumentsDbContext db) : IRequestHandler<Query, Result<Page<PrivateDocumentItem>>>
    {
        public async ValueTask<Result<Page<PrivateDocumentItem>>> Handle(Query query, CancellationToken cancellationToken)
        {
            var page = PagingRules.ParsePage(query.Page);
            var pageSize = PagingRules.ParsePageSize(query.PageSize, DefaultPageSize);

            var documents = db.Documents.AsNoTracking().Where(d => d.Visibility == Visibility.Private);
            var total = await documents.CountAsync(cancellationToken);
            var rows = await documents
                .OrderByDescending(d => d.UpdatedAt).ThenBy(d => d.Id)
                .Skip(PagingRules.Skip(page, pageSize))
                .Take(pageSize)
                .Select(d => new
                {
                    d.Id,
                    d.Title,
                    d.CreatedBy,
                    d.Status,
                    LatestVersionStatus = d.Versions.OrderByDescending(v => v.Number).Select(v => (VersionStatus?)v.Status).FirstOrDefault(),
                    d.CreatedAt,
                    d.UpdatedAt,
                })
                .ToListAsync(cancellationToken);

            var items = rows
                .Select(r => new PrivateDocumentItem(
                    r.Id,
                    r.Title,
                    r.CreatedBy,
                    r.Status.ToString().ToLowerInvariant(),
                    r.LatestVersionStatus?.ToString().ToLowerInvariant(),
                    r.CreatedAt,
                    r.UpdatedAt))
                .ToList();
            return new Page<PrivateDocumentItem>(items, page, pageSize, total);
        }
    }
}
