using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using RagAssistant.Modules.Documents.Common;
using RagAssistant.Modules.Documents.Domain;
using RagAssistant.Modules.Documents.Persistence;
using RagAssistant.SharedKernel.Common;

namespace RagAssistant.Modules.Documents.Features.ListDocuments;

internal static class ListDocuments
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;
    public const int MaxQueryLength = 200;
    public const int MaxTags = 20;

    private static readonly string[] Statuses = ["active", "queued", "processing", "failed", "archived"];

    private static readonly string[] Sorts = ["title", "-title", "updated", "-updated", "effectiveDate", "-effectiveDate"];

    // Raw strings on purpose: a malformed value must surface as a validation.failed field error (AC-7).
    public sealed record Query(
        string? Q,
        string? Status,
        string? Language,
        string? Owner,
        string[] Tags,
        string? EffectiveFrom,
        string? EffectiveTo,
        string? Sort,
        string? Page,
        string? PageSize) : IRequest<Result<Page<DocumentSummary>>>;

    public sealed record DocumentSummary(
        Guid Id,
        string Title,
        string OwnerName,
        string Language,
        string[] Tags,
        DateOnly? EffectiveDate,
        string Status,
        int? ActiveVersionNumber,
        int? LatestVersionNumber,
        int? PassageCount,
        DateTimeOffset LastActivityAt,
        DateTimeOffset CreatedAt);

    public sealed class Validator : AbstractValidator<Query>
    {
        public Validator()
        {
            this.AddPagingRules(x => x.Page, x => x.PageSize, MaxPageSize);

            RuleFor(x => x.Q).MaximumLength(MaxQueryLength);

            RuleFor(x => x.Status)
                .Must(v => string.IsNullOrWhiteSpace(v) || Statuses.Contains(v))
                .WithMessage($"'status' must be one of: {string.Join(", ", Statuses)}.");

            RuleFor(x => x.Language)
                .Must(v => string.IsNullOrWhiteSpace(v) || (v.Trim().Length == 2 && v.Trim().All(char.IsAsciiLetter)))
                .WithMessage("'language' must be two letters.");

            RuleFor(x => x.Tags)
                .Must(v => v.Length <= MaxTags)
                .WithMessage($"At most {MaxTags} tags can be combined.");

            RuleFor(x => x.EffectiveFrom)
                .Must(v => DocumentMetadata.TryParseDate(v, out _))
                .WithMessage($"'effectiveFrom' must be a date formatted {DocumentMetadata.DateFormat}.");

            RuleFor(x => x.EffectiveTo)
                .Must(v => DocumentMetadata.TryParseDate(v, out _))
                .WithMessage($"'effectiveTo' must be a date formatted {DocumentMetadata.DateFormat}.");

            RuleFor(x => x)
                .Must(x =>
                {
                    _ = DocumentMetadata.TryParseDate(x.EffectiveFrom, out var from);
                    _ = DocumentMetadata.TryParseDate(x.EffectiveTo, out var to);
                    return from is null || to is null || from <= to;
                })
                .WithMessage("'effectiveFrom' must not be after 'effectiveTo'.")
                .OverridePropertyName("effectiveFrom");

            RuleFor(x => x.Sort)
                .Must(v => string.IsNullOrWhiteSpace(v) || Sorts.Contains(v))
                .WithMessage($"'sort' must be one of: {string.Join(", ", Sorts)}.");
        }
    }

    internal sealed class Handler(DocumentsDbContext db) : IRequestHandler<Query, Result<Page<DocumentSummary>>>
    {
        public async ValueTask<Result<Page<DocumentSummary>>> Handle(Query query, CancellationToken cancellationToken)
        {
            var page = PagingRules.ParsePage(query.Page);
            var pageSize = PagingRules.ParsePageSize(query.PageSize, DefaultPageSize);

            // One query: the latest and active version facts are correlated subqueries, never loaded entities.
            var rows = db.Documents
                .AsNoTracking()
                .Select(d => new
                {
                    Doc = d,
                    Latest = d.Versions.OrderByDescending(v => v.Number).Select(v => (VersionStatus?)v.Status).FirstOrDefault(),
                    LatestNumber = d.Versions.OrderByDescending(v => v.Number).Select(v => (int?)v.Number).FirstOrDefault(),
                    ActiveNumber = d.Versions.Where(v => v.Id == d.ActiveVersionId).Select(v => (int?)v.Number).FirstOrDefault(),
                    PassageCount = d.Versions.Where(v => v.Id == d.ActiveVersionId).Select(v => v.PassageCount).FirstOrDefault(),
                    IndexedAt = d.Versions.Where(v => v.Id == d.ActiveVersionId).Select(v => v.IndexedAt).FirstOrDefault(),
                })
                .Select(x => new
                {
                    x.Doc,
                    x.LatestNumber,
                    x.ActiveNumber,
                    x.PassageCount,
                    // Must stay in step with DocumentStatusRules.Derive.
                    Status = x.Doc.Status == DocumentStatus.Archived ? "archived"
                        : x.Latest == VersionStatus.Queued ? "queued"
                        : x.Latest == VersionStatus.Processing ? "processing"
                        : x.Latest == VersionStatus.Failed ? "failed"
                        : "active",
                    LastActivity = x.IndexedAt != null && x.IndexedAt > x.Doc.UpdatedAt ? x.IndexedAt.Value : x.Doc.UpdatedAt,
                });

            var status = query.Status?.Trim();
            rows = string.IsNullOrEmpty(status)
                ? rows.Where(x => x.Status != "archived")
                : rows.Where(x => x.Status == status);

            var title = query.Q?.Trim();
            if (!string.IsNullOrEmpty(title))
            {
                var pattern = "%" + title.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
                rows = rows.Where(x => EF.Functions.ILike(x.Doc.Title, pattern, "\\"));
            }

            if (!string.IsNullOrWhiteSpace(query.Language))
            {
                var language = query.Language.Trim().ToLowerInvariant();
                rows = rows.Where(x => x.Doc.Language == language);
            }

            if (!string.IsNullOrWhiteSpace(query.Owner))
            {
                var owner = query.Owner;
                rows = rows.Where(x => x.Doc.OwnerName == owner);
            }

            foreach (var tag in DocumentMetadata.NormalizeTags(query.Tags.Where(t => !string.IsNullOrWhiteSpace(t))))
            {
                rows = rows.Where(x => x.Doc.Tags.Contains(tag));
            }

            _ = DocumentMetadata.TryParseDate(query.EffectiveFrom, out var from);
            _ = DocumentMetadata.TryParseDate(query.EffectiveTo, out var to);
            if (from is { } fromDate)
            {
                rows = rows.Where(x => x.Doc.EffectiveDate >= fromDate);
            }

            if (to is { } toDate)
            {
                rows = rows.Where(x => x.Doc.EffectiveDate <= toDate);
            }

            var total = await rows.CountAsync(cancellationToken);

            var ordered = query.Sort switch
            {
                "title" => rows.OrderBy(x => x.Doc.Title),
                "-title" => rows.OrderByDescending(x => x.Doc.Title),
                "updated" => rows.OrderBy(x => x.LastActivity),
                "effectiveDate" => rows.OrderBy(x => x.Doc.EffectiveDate == null).ThenBy(x => x.Doc.EffectiveDate),
                "-effectiveDate" => rows.OrderBy(x => x.Doc.EffectiveDate == null).ThenByDescending(x => x.Doc.EffectiveDate),
                _ => rows.OrderByDescending(x => x.LastActivity),
            };

            var items = await ordered
                .ThenBy(x => x.Doc.Id)
                .Skip(PagingRules.Skip(page, pageSize))
                .Take(pageSize)
                .Select(x => new DocumentSummary(
                    x.Doc.Id, x.Doc.Title, x.Doc.OwnerName, x.Doc.Language, x.Doc.Tags, x.Doc.EffectiveDate, x.Status,
                    x.ActiveNumber, x.LatestNumber, x.PassageCount, x.LastActivity, x.Doc.CreatedAt))
                .ToListAsync(cancellationToken);

            return new Page<DocumentSummary>(items, page, pageSize, total);
        }
    }
}
