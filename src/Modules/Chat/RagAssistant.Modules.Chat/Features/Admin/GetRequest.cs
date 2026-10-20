using System.Text.Json;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RagAssistant.Modules.Chat.Common;
using RagAssistant.Modules.Chat.Domain;
using RagAssistant.Modules.Chat.Options;
using RagAssistant.Modules.Chat.Persistence;
using RagAssistant.SharedKernel.Common;
using RagAssistant.SharedKernel.Contracts.Documents;

namespace RagAssistant.Modules.Chat.Features.Admin;

/// <summary>
/// Spec 017, the inspector: one trace with the feedback on its answer. Document titles come from the current shared titles, never
/// from the title stored in the trace, so a private document is never named to a maintainer.
/// </summary>
internal static partial class GetRequest
{
    private static readonly JsonSerializerOptions TraceJson = new(JsonSerializerDefaults.Web);

    public sealed record Query(Guid Id) : IRequest<Result<RequestDetail>>;

    internal sealed class Handler(
        ChatDbContext db, IDocumentTitles titles, IOptions<AdminOptions> admin, ILogger<Handler> logger)
        : IRequestHandler<Query, Result<RequestDetail>>
    {
        public async ValueTask<Result<RequestDetail>> Handle(Query query, CancellationToken cancellationToken)
        {
            var trace = await db.RequestTraces.AsNoTracking().SingleOrDefaultAsync(t => t.Id == query.Id, cancellationToken);
            if (trace is null)
            {
                return ChatErrors.RequestNotFound;
            }

            var stored = JsonSerializer.Deserialize<List<RetrievedPassageTrace>>(trace.RetrievedPassages, TraceJson) ?? [];
            var documentIds = trace.FilterDocumentIds
                .Concat(stored.Where(p => p.DocumentId is not null).Select(p => p.DocumentId!.Value))
                .Distinct()
                .ToArray();

            IReadOnlyDictionary<Guid, string> shared = new Dictionary<Guid, string>();
            var unavailable = false;
            try
            {
                shared = await titles.GetSharedTitlesAsync(documentIds, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Spec 017: a failed title lookup must not hide the trace; the titles are shown as unavailable.
                LogTitlesFailed(logger, ex, trace.Id);
                unavailable = true;
            }

            var rating = await db.MessageRatings.AsNoTracking()
                .Where(r => r.MessageId == trace.AssistantMessageId)
                .Select(r => (FeedbackRating?)r.Rating)
                .SingleOrDefaultAsync(cancellationToken);
            var reports = await db.IssueReports.AsNoTracking()
                .Where(r => r.MessageId == trace.AssistantMessageId)
                .OrderBy(r => r.CreatedAt)
                .Select(r => new { r.Category, r.Comment, r.CreatedAt })
                .ToListAsync(cancellationToken);

            string? TitleOf(Guid? id) => id is { } known && shared.TryGetValue(known, out var title) ? title : null;

            var passages = stored
                .OrderBy(p => p.Rank)
                .Select(p => new RequestPassage(
                    p.Rank, p.Marker, p.Score, p.Cited, p.PassageId, p.DocumentId, TitleOf(p.DocumentId), p.VersionNumber,
                    p.SectionPath ?? [], p.PageStart, p.PageEnd,
                    p.AdmittedBy is null ? null
                        : new RequestPassageStages(p.VectorRank, p.Cosine, p.KeywordRank, p.KeywordScore, p.FusedScore, p.RerankScore, p.AdmittedBy)))
                .ToList();

            return new RequestDetail(
                trace.Id,
                trace.ConversationId,
                trace.UserMessageId,
                trace.AssistantMessageId,
                trace.Attempt,
                trace.OwnerSub,
                trace.StartedAt,
                new RequestQuestion(
                    trace.QuestionText,
                    trace.FilterLanguage,
                    trace.FilterTags,
                    [.. trace.FilterDocumentIds.Select(id => new ScopedDocument(id, TitleOf(id)))],
                    trace.UnavailableDocumentIds,
                    trace.QuestionLanguage,
                    trace.QuestionLanguageSource?.ToApi()),
                new RequestRewrite(
                    trace.RewriteUsed, trace.RetrievalQuery, trace.RewriteFallbackReason?.ToApi(), trace.RewriteMs,
                    trace.RewriteInputTokens, trace.RewriteOutputTokens, trace.RewritePromptVersion),
                new RequestRetrieval(
                    trace.EvidenceOutcome, trace.MinScore, trace.ConfidentScore, trace.DuplicateOverlap, trace.BestScore,
                    trace.EmbeddingModel, trace.RetrievalMs, passages, trace.RetrievalMode, trace.RerankStatus, trace.RerankMs, trace.RerankModel),
                new RequestGeneration(
                    trace.ChatModel, trace.PromptVersion, trace.InputTokens, trace.OutputTokens, trace.SentinelHit,
                    trace.StrippedMarkerCount, trace.FirstTokenMs, trace.TotalMs, trace.AnswerLanguage, trace.TrimmedPassageCount,
                    trace.PromptTokenEstimate),
                new RequestResult(trace.FinalStatus.ToApi(), trace.RefusalReason?.ToApi(), trace.ErrorCode),
                new RequestFeedback(
                    rating?.ToApi(),
                    [.. reports.Select(r => new FeedbackReport(r.Category.ToApi(), r.Comment, r.CreatedAt))]),
                trace.OtelTraceId,
                admin.Value.TraceUrl(trace.OtelTraceId),
                unavailable);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not look up document titles for request {RequestId}; the inspector shows them as unavailable")]
    private static partial void LogTitlesFailed(ILogger logger, Exception exception, Guid requestId);
}
