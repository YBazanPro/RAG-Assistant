using System.Text.Json;
using Mediator;
using Microsoft.EntityFrameworkCore;
using RagAssistant.Modules.Chat.Common;
using RagAssistant.Modules.Chat.Domain;
using RagAssistant.Modules.Chat.Persistence;
using RagAssistant.SharedKernel.Common;
using RagAssistant.SharedKernel.Contracts.Documents;

namespace RagAssistant.Modules.Chat.Features.GetActivity;

/// <summary>
/// What the pipeline did for one answer (spec 015), read from the stored trace of its latest attempt. Only recorded facts: no
/// passage text, prompt, token counts or model names.
/// </summary>
internal static class GetActivity
{
    private static readonly JsonSerializerOptions TraceJson = new(JsonSerializerDefaults.Web);

    public sealed record Query(string UserId, Guid ConversationId, Guid MessageId) : IRequest<Result<ActivityView>>;

    public sealed record ActivityView(
        string Question, RewriteStep Rewrite, ScopeStep Scope, RetrievalStep Retrieval, AnswerStep Answer);

    public sealed record RewriteStep(bool Used, string? Query, string? FallbackReason, int? Ms);

    public sealed record ScopeStep(string? Language, IReadOnlyList<string> Tags, IReadOnlyList<ScopeDocument> Documents);

    public sealed record ScopeDocument(Guid Id, string? Title, bool Available);

    /// <param name="BestScore">The best candidate before the evidence rules; explains a refusal (null when nothing matched, or on older traces).</param>
    public sealed record RetrievalStep(
        string? Outcome, double? MinScore, double? ConfidentScore, int? Ms, IReadOnlyList<PassageStep> Passages, double? BestScore = null);

    public sealed record PassageStep(
        int Rank,
        int Marker,
        double Score,
        bool Cited,
        Guid? DocumentId,
        string? DocumentTitle,
        int? VersionNumber,
        IReadOnlyList<string> SectionPath,
        int? PageStart,
        int? PageEnd);

    public sealed record AnswerStep(
        bool ModelCalled,
        string Status,
        string? RefusalReason,
        string? ErrorCode,
        int? FirstTokenMs,
        int TotalMs,
        int StrippedMarkerCount,
        bool SentinelHit);

    internal sealed class Handler(ChatDbContext db, IDocumentCatalog catalog) : IRequestHandler<Query, Result<ActivityView>>
    {
        public async ValueTask<Result<ActivityView>> Handle(Query query, CancellationToken cancellationToken)
        {
            // Someone else's conversation is "not found", like the rest of Chat; so is a message that has no trace yet.
            var trace = await (
                from t in db.RequestTraces.AsNoTracking()
                join c in db.Conversations.AsNoTracking() on t.ConversationId equals c.Id
                where c.Id == query.ConversationId && c.OwnerSub == query.UserId && t.AssistantMessageId == query.MessageId
                orderby t.Attempt descending
                select t).FirstOrDefaultAsync(cancellationToken);
            if (trace is null)
            {
                return ChatErrors.ActivityNotFound;
            }

            var stored = JsonSerializer.Deserialize<List<RetrievedPassageTrace>>(trace.RetrievedPassages, TraceJson) ?? [];
            var passageDocumentIds = stored.Where(p => p.DocumentId is not null).Select(p => p.DocumentId!.Value);
            var availability = await catalog.CheckAsync([.. trace.FilterDocumentIds, .. passageDocumentIds], query.UserId, cancellationToken);

            var documents = trace.FilterDocumentIds
                .Select(id => new ScopeDocument(
                    id,
                    availability.Titles.GetValueOrDefault(id),
                    availability.Searchable.Contains(id) && !trace.UnavailableDocumentIds.Contains(id)))
                .ToList();
            var passages = stored
                .OrderBy(p => p.Rank)
                .Select(p => new PassageStep(
                    p.Rank, p.Marker, p.Score, p.Cited, p.DocumentId,
                    // A cited passage was authorised when the answer was given and keeps its snapshot title; the others
                    // show a title only while the viewer can still read the document (spec 016).
                    p.Cited || (p.DocumentId is { } id && availability.Known.Contains(id)) ? p.DocumentTitle : null, p.VersionNumber, p.SectionPath ?? [], p.PageStart, p.PageEnd))
                .ToList();

            return new ActivityView(
                trace.QuestionText,
                new RewriteStep(trace.RewriteUsed, trace.RetrievalQuery, trace.RewriteFallbackReason?.ToApi(), trace.RewriteMs),
                new ScopeStep(trace.FilterLanguage, trace.FilterTags, documents),
                new RetrievalStep(trace.EvidenceOutcome is { } outcome ? JsonNamingPolicy.CamelCase.ConvertName(outcome) : null, trace.MinScore, trace.ConfidentScore, trace.RetrievalMs, passages, trace.BestScore),
                new AnswerStep(
                    trace.ChatModel is not null,
                    trace.FinalStatus.ToApi(),
                    trace.RefusalReason?.ToApi(),
                    trace.ErrorCode,
                    trace.FirstTokenMs,
                    trace.TotalMs,
                    trace.StrippedMarkerCount,
                    trace.SentinelHit));
        }
    }
}
