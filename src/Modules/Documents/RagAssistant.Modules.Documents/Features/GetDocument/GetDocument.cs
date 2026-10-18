using Mediator;
using Microsoft.EntityFrameworkCore;
using RagAssistant.Modules.Documents.Common;
using RagAssistant.Modules.Documents.Domain;
using RagAssistant.Modules.Documents.Persistence;
using RagAssistant.SharedKernel.Common;

namespace RagAssistant.Modules.Documents.Features.GetDocument;

internal static class GetDocument
{
    public sealed record Query(Guid DocumentId, string ViewerSub) : IRequest<Result<Response>>;

    public sealed record LatestVersion(
        Guid Id,
        int Number,
        string Status,
        string SourceFormat,
        string OriginalFileName,
        int SizeBytes,
        int? PassageCount,
        DateTimeOffset CreatedAt,
        int[]? PagesWithoutText);

    public sealed record LatestRun(
        Guid Id,
        int Attempt,
        string Status,
        string Stage,
        string? ErrorCode,
        string? ErrorMessage,
        DateTimeOffset QueuedAt,
        DateTimeOffset? StartedAt,
        DateTimeOffset? FinishedAt);

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
        LatestRun? LatestRun,
        DateTimeOffset CreatedAt,
        string? CreatedBy,
        DateTimeOffset UpdatedAt,
        string? UpdatedBy,
        int Revision,
        string Visibility,
        bool IsOwner,
        string LanguageSource,
        string? DetectedLanguage,
        string? LanguageDetection);

    internal sealed class Handler(DocumentsDbContext db) : IRequestHandler<Query, Result<Response>>
    {
        public async ValueTask<Result<Response>> Handle(Query query, CancellationToken cancellationToken)
        {
            // Projected so the version's Content column (up to the import limit) is never loaded.
            var row = await db.Documents
                .AsNoTracking()
                .ReadableBy(query.ViewerSub)
                .Where(d => d.Id == query.DocumentId)
                .Select(d => new
                {
                    Document = d,
                    Latest = d.Versions
                        .OrderByDescending(v => v.Number)
                        .Select(v => new VersionRow(v.Id, v.Number, v.Status, v.SourceFormat, v.OriginalFileName, v.SizeBytes, v.PassageCount, v.CreatedAt, v.PagesWithoutText))
                        .FirstOrDefault(),
                    // What the active version detected, so the detail page can offer it when the owner chose otherwise (spec 018).
                    Detection = d.Versions
                        .Where(v => v.Id == d.ActiveVersionId)
                        .Select(v => new DetectionRow(v.DetectedLanguage, v.LanguageDetection))
                        .FirstOrDefault(),
                    Run = d.Versions
                        .OrderByDescending(v => v.Number)
                        .Take(1)
                        .SelectMany(v => v.Runs)
                        .OrderByDescending(r => r.Attempt)
                        .Select(r => new RunRow(
                            r.Id, r.Attempt, r.Status, r.CurrentStage, r.ErrorCode, r.ErrorMessage, r.QueuedAt, r.StartedAt, r.FinishedAt))
                        .FirstOrDefault(),
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (row is null)
            {
                return DocumentErrors.NotFound;
            }

            var d = row.Document;
            var isOwner = d.IsOwner(query.ViewerSub);
            var latest = row.Latest is { } v
                ? new LatestVersion(v.Id, v.Number, v.Status.ToApi(), v.SourceFormat.ToApi(), v.OriginalFileName, v.SizeBytes, v.PassageCount, v.CreatedAt, v.PagesWithoutText)
                : null;
            var run = row.Run is { } r
                ? new LatestRun(r.Id, r.Attempt, r.Status.ToApi(), r.Stage.ToApi(), r.ErrorCode, r.ErrorMessage, r.QueuedAt, r.StartedAt, r.FinishedAt)
                : null;

            return new Response(
                d.Id, d.Title, d.OwnerName, d.Language, d.Tags, d.EffectiveDate, d.Source,
                DocumentStatusRules.Derive(d.Status, row.Latest?.Status),
                d.ActiveVersionId, latest, run, d.CreatedAt,
                // The raw sub is the access identity: only its owner gets it back (spec 016).
                isOwner ? d.CreatedBy : null, d.UpdatedAt, isOwner || d.UpdatedBy == query.ViewerSub ? d.UpdatedBy : null, d.Revision,
                d.Visibility.ToApi(), isOwner, d.LanguageSource.ToApi(), row.Detection?.Language, row.Detection?.Confidence.ToApi());
        }

        private sealed record VersionRow(
            Guid Id, int Number, VersionStatus Status, SourceFormat SourceFormat, string OriginalFileName, int SizeBytes, int? PassageCount, DateTimeOffset CreatedAt, int[]? PagesWithoutText);

        private sealed record DetectionRow(string? Language, LanguageConfidence? Confidence);

        private sealed record RunRow(
            Guid Id, int Attempt, RunStatus Status, IngestionStage Stage, string? ErrorCode, string? ErrorMessage,
            DateTimeOffset QueuedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt);
    }
}
