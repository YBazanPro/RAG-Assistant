using Mediator;
using Microsoft.EntityFrameworkCore;
using RagAssistant.Modules.Documents.Common;
using RagAssistant.Modules.Documents.Domain;
using RagAssistant.Modules.Documents.Persistence;
using RagAssistant.Shared.Common;

namespace RagAssistant.Modules.Documents.Features.GetDocument;

internal static class GetDocument
{
    public sealed record Query(Guid DocumentId) : IRequest<Result<Response>>;

    public sealed record LatestVersion(
        Guid Id,
        int Number,
        string Status,
        string SourceFormat,
        string OriginalFileName,
        int SizeBytes,
        DateTimeOffset CreatedAt);

    public sealed record Response(
        Guid Id,
        string Title,
        string OwnerName,
        string Language,
        string[] Tags,
        DateOnly? EffectiveDate,
        string? Source,
        string Status,
        Guid? ActiveVersionId,
        LatestVersion? LatestVersion,
        DateTimeOffset CreatedAt,
        string CreatedBy,
        DateTimeOffset UpdatedAt,
        string UpdatedBy);

    internal sealed class Handler(DocumentsDbContext db) : IRequestHandler<Query, Result<Response>>
    {
        private static readonly Error NotFound = Error.NotFound("documents.not_found", "The document was not found.");

        public async ValueTask<Result<Response>> Handle(Query query, CancellationToken cancellationToken)
        {
            // Projected so the version's Content column (up to the import limit) is never loaded.
            var row = await db.Documents
                .AsNoTracking()
                .Where(d => d.Id == query.DocumentId)
                .Select(d => new
                {
                    Document = d,
                    Latest = d.Versions
                        .OrderByDescending(v => v.Number)
                        .Select(v => new VersionRow(v.Id, v.Number, v.Status, v.SourceFormat, v.OriginalFileName, v.SizeBytes, v.CreatedAt))
                        .FirstOrDefault(),
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (row is null)
            {
                return NotFound;
            }

            var d = row.Document;
            var latest = row.Latest is { } v
                ? new LatestVersion(v.Id, v.Number, v.Status.ToApi(), v.SourceFormat.ToApi(), v.OriginalFileName, v.SizeBytes, v.CreatedAt)
                : null;

            return new Response(
                d.Id, d.Title, d.OwnerName, d.Language, d.Tags, d.EffectiveDate, d.Source,
                DocumentStatusRules.Derive(d.Status, row.Latest?.Status),
                d.ActiveVersionId, latest, d.CreatedAt, d.CreatedBy, d.UpdatedAt, d.UpdatedBy);
        }

        private sealed record VersionRow(
            Guid Id, int Number, VersionStatus Status, SourceFormat SourceFormat, string OriginalFileName, int SizeBytes, DateTimeOffset CreatedAt);
    }
}
