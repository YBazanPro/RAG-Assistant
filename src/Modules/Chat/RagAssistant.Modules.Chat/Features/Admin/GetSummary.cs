using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using RagAssistant.Modules.Chat.Common;
using RagAssistant.Modules.Chat.Domain;
using RagAssistant.Modules.Chat.Persistence;
using RagAssistant.SharedKernel.Common;

namespace RagAssistant.Modules.Chat.Features.Admin;

/// <summary>Spec 017: aggregates over the traces of a time window, computed in SQL.</summary>
internal static class GetSummary
{
    public const string DefaultWindow = "7d";

    public static readonly IReadOnlyDictionary<string, TimeSpan> Windows = new Dictionary<string, TimeSpan>
    {
        ["24h"] = TimeSpan.FromHours(24),
        ["7d"] = TimeSpan.FromDays(7),
        ["30d"] = TimeSpan.FromDays(30),
    };

    public sealed record Query(string? Window) : IRequest<Result<ChatSummary>>;

    public sealed class Validator : AbstractValidator<Query>
    {
        public Validator() =>
            RuleFor(q => q.Window)
                .Must(w => string.IsNullOrEmpty(w) || Windows.ContainsKey(w))
                .WithMessage("'window' must be one of 24h, 7d, 30d.")
                .OverridePropertyName("window");
    }

    /// <summary>Column aliases must match these names exactly, hence the quotes in the query.</summary>
    private sealed class PercentileRow
    {
        public double? FirstTokenP50 { get; set; }

        public double? FirstTokenP95 { get; set; }

        public double? TotalP50 { get; set; }

        public double? TotalP95 { get; set; }
    }

    internal sealed class Handler(ChatDbContext db, TimeProvider clock) : IRequestHandler<Query, Result<ChatSummary>>
    {
        public async ValueTask<Result<ChatSummary>> Handle(Query query, CancellationToken cancellationToken)
        {
            var window = string.IsNullOrEmpty(query.Window) ? DefaultWindow : query.Window;
            var since = clock.GetUtcNow() - Windows[window];
            var traces = db.RequestTraces.AsNoTracking().Where(t => t.StartedAt >= since);

            var byStatus = await traces.GroupBy(t => t.FinalStatus).Select(g => new { Key = g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
            var byOutcome = await traces.GroupBy(t => t.EvidenceOutcome).Select(g => new { Key = g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
            var errorCodes = await traces.Where(t => t.ErrorCode != null)
                .GroupBy(t => t.ErrorCode!).Select(g => new { Key = g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
            var fallbacks = await traces.Where(t => t.RewriteFallbackReason != null)
                .GroupBy(t => t.RewriteFallbackReason!.Value).Select(g => new { Key = g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
            var negative = await traces.CountAsync(t =>
                db.MessageRatings.Any(r => r.MessageId == t.AssistantMessageId && r.Rating == FeedbackRating.NotHelpful)
                || db.IssueReports.Any(r => r.MessageId == t.AssistantMessageId), cancellationToken);

            // EF cannot express percentile_cont, and the spec forbids loading rows to compute it, so this one query is SQL.
            var percentiles = await db.Database.SqlQuery<PercentileRow>($"""
                SELECT
                    percentile_cont(0.5) WITHIN GROUP (ORDER BY first_token_ms) AS "FirstTokenP50",
                    percentile_cont(0.95) WITHIN GROUP (ORDER BY first_token_ms) AS "FirstTokenP95",
                    percentile_cont(0.5) WITHIN GROUP (ORDER BY total_ms) AS "TotalP50",
                    percentile_cont(0.95) WITHIN GROUP (ORDER BY total_ms) AS "TotalP95"
                FROM chat.request_traces
                WHERE started_at >= {since}
                """).SingleAsync(cancellationToken);

            var requests = byStatus.Sum(s => s.Count);
            var refused = byStatus.Where(s => s.Key == AnswerStatus.Refused).Sum(s => s.Count);

            var errors = errorCodes
                .Select(e => new ErrorCount(ErrorStages.StageOf(e.Key), e.Key, e.Count))
                .Concat(fallbacks.Select(f => new ErrorCount(ErrorStages.Rewrite, ErrorStages.RewriteCode(f.Key), f.Count)))
                .OrderByDescending(e => e.Count).ThenBy(e => e.ErrorCode, StringComparer.Ordinal)
                .ToList();

            return new ChatSummary(
                window,
                since,
                requests,
                byStatus.ToDictionary(s => s.Key.ToApi(), s => s.Count),
                requests == 0 ? 0 : (double)refused / requests,
                byOutcome.Where(o => o.Key is not null).ToDictionary(o => o.Key!, o => o.Count),
                new Percentiles(percentiles.FirstTokenP50, percentiles.FirstTokenP95),
                new Percentiles(percentiles.TotalP50, percentiles.TotalP95),
                errors,
                negative);
        }
    }
}
