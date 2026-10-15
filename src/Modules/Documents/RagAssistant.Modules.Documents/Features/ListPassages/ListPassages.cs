using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using RagAssistant.Modules.Documents.Common;
using RagAssistant.Modules.Documents.Persistence;
using RagAssistant.Shared.Common;

namespace RagAssistant.Modules.Documents.Features.ListPassages;

internal static class ListPassages
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    // Raw strings on purpose, as in ListDocuments: malformed values are validation.failed field errors.
    public sealed record Query(Guid DocumentId, int Number, string? Page, string? PageSize, string? Focus)
        : IRequest<Result<Response>>;

    public sealed record PassageItem(
        Guid Id,
        int Position,
        string[] SectionPath,
        int SectionOrdinal,
        string Text,
        int CharCount,
        int TokenEstimate);

    public sealed record Response(
        int VersionNumber,
        string VersionStatus,
        bool IsActive,
        IReadOnlyList<PassageItem> Items,
        int Page,
        int PageSize,
        int TotalCount);

    public sealed class Validator : AbstractValidator<Query>
    {
        public Validator()
        {
            this.AddPagingRules(x => x.Page, x => x.PageSize, MaxPageSize);

            RuleFor(x => x.Focus)
                .Must(v => string.IsNullOrWhiteSpace(v) || Guid.TryParse(v, out _))
                .WithMessage("'focus' must be a passage id.");
        }
    }

    internal sealed class Handler(DocumentsDbContext db) : IRequestHandler<Query, Result<Response>>
    {
        public async ValueTask<Result<Response>> Handle(Query query, CancellationToken cancellationToken)
        {
            var document = await db.Documents.AsNoTracking()
                .Where(d => d.Id == query.DocumentId)
                .Select(d => new { d.ActiveVersionId })
                .FirstOrDefaultAsync(cancellationToken);

            if (document is null)
            {
                return DocumentErrors.NotFound;
            }

            var version = await db.Versions.AsNoTracking()
                .Where(v => v.DocumentId == query.DocumentId && v.Number == query.Number)
                .Select(v => new { v.Id, v.Status })
                .FirstOrDefaultAsync(cancellationToken);

            if (version is null)
            {
                return DocumentErrors.VersionNotFound;
            }

            var page = PagingRules.ParsePage(query.Page);
            var pageSize = PagingRules.ParsePageSize(query.PageSize, DefaultPageSize);
            var passages = db.Passages.AsNoTracking().Where(p => p.VersionId == version.Id);

            if (!string.IsNullOrWhiteSpace(query.Focus))
            {
                var focusId = Guid.Parse(query.Focus);
                var position = await passages
                    .Where(p => p.Id == focusId)
                    .Select(p => (int?)p.Position)
                    .FirstOrDefaultAsync(cancellationToken);

                if (position is null)
                {
                    return DocumentErrors.PassageNotFound;
                }

                var before = await passages.CountAsync(p => p.Position < position, cancellationToken);
                page = (before / pageSize) + 1;
            }

            var total = await passages.CountAsync(cancellationToken);

            // Embeddings are never projected.
            var items = await passages
                .OrderBy(p => p.Position)
                .Skip(PagingRules.Skip(page, pageSize))
                .Take(pageSize)
                .Select(p => new PassageItem(p.Id, p.Position, p.SectionPath, p.SectionOrdinal, p.Text, p.CharCount, p.TokenEstimate))
                .ToListAsync(cancellationToken);

            return new Response(
                query.Number,
                version.Status.ToApi(),
                version.Id == document.ActiveVersionId,
                items,
                page,
                pageSize,
                total);
        }
    }
}
