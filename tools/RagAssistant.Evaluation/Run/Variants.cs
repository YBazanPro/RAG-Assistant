using RagAssistant.Evaluation.Api;
using RagAssistant.Evaluation.Golden;

namespace RagAssistant.Evaluation.Run;

/// <summary>A retrieval configuration measured by <c>run --variants</c> (spec 019) through the search endpoint's overrides.</summary>
internal sealed record VariantDefinition(string Name, string Mode, bool Rerank);

/// <param name="Outcome">The search's evidence outcome: noEvidence, lowConfidence or sufficient.</param>
/// <param name="Rerank">off, applied or skipped, as the search reported it.</param>
/// <param name="SearchMs">The whole search as the harness timed it, query embedding included.</param>
internal sealed record VariantCase(
    string Id,
    string Category,
    string QuestionLanguage,
    string ExpectedBehaviour,
    string Outcome,
    string? Rerank,
    int SearchMs,
    int? RerankMs,
    bool BehaviourCorrect,
    CaseMetrics Metrics);

/// <param name="Behaviour">What the retrieval outcome alone predicts, as in <c>sweep</c>: no evidence → refusal, low confidence → partial, else answer.</param>
internal sealed record VariantAggregate(
    int Cases,
    double? RecallAt1,
    double? RecallAt3,
    double? RecallAt5,
    double? RecallAtTopK,
    double? Mrr,
    double? Behaviour,
    double? Refusals,
    double? MedianSearchMs,
    double? MedianRerankMs,
    int RerankSkipped);

internal sealed record VariantResult(
    string Name,
    string Mode,
    bool Rerank,
    VariantAggregate Overall,
    IReadOnlyDictionary<string, VariantAggregate> ByCategory,
    IReadOnlyDictionary<string, VariantAggregate> ByQuestionLanguage,
    IReadOnlyList<VariantCase> Cases);

/// <summary>
/// Spec 019, harness: retrieval measured for vector, hybrid and hybrid + rerank on the same cases and the same retrieval queries
/// (the chat run's standalone question for a follow-up). Answer metrics stay one run per configuration; these are retrieval only.
/// </summary>
internal static class Variants
{
    public static readonly VariantDefinition[] All =
    [
        new("vector", "vector", false),
        new("hybrid", "hybrid", false),
        new("hybrid+rerank", "hybrid", true),
    ];

    public static string Predicted(string outcome) => outcome switch
    {
        "noEvidence" => "refusal",
        "lowConfidence" => "partial",
        _ => "answer",
    };

    public static async Task<IReadOnlyList<VariantResult>> RunAsync(
        RagAssistantApi api,
        string user,
        string tag,
        int topK,
        IReadOnlyList<(GoldenCase Golden, CaseResult Result)> cases,
        CorpusPassages passages,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var results = new List<VariantResult>();
        foreach (var variant in All)
        {
            var measured = new List<VariantCase>();
            foreach (var (golden, result) in cases)
            {
                if (result.RetrievalQuery is not { Length: > 0 } query)
                {
                    continue;
                }

                // The keyword configuration follows the question's written language, as in the chat (spec 019).
                var language = result.QuestionLanguageDetected ?? golden.QuestionLanguage;
                var (search, ms) = await api.SearchVariantAsync(user, query, tag, topK, variant.Mode, variant.Rerank, language, cancellationToken);
                var retrieved = (search.Passages ?? [])
                    .Select(p => new RetrievedPassage(p.Rank, p.PassageId, passages.DocumentOf(p.PassageId), p.Score, null, passages.MatchOf(golden, p.PassageId)))
                    .ToList();
                var metrics = Scoring.Score(
                    golden.Expected.Behaviour, language, golden.Expected.Evidence.Count, "retrieval", retrieved, [], "", topK, answerMetrics: false);
                measured.Add(new VariantCase(
                    golden.Id, golden.Category, golden.QuestionLanguage, golden.Expected.Behaviour, search.Outcome, search.Rerank, ms,
                    search.RerankMs, Predicted(search.Outcome) == golden.Expected.Behaviour, metrics));
            }

            var overall = Aggregate(measured);
            results.Add(new VariantResult(
                variant.Name,
                variant.Mode,
                variant.Rerank,
                overall,
                Group(measured, c => c.Category),
                Group(measured, c => c.QuestionLanguage),
                measured));
            await output.WriteLineAsync(
                $"variant    {variant.Name}: R@5 {Num(overall.RecallAt5)}, MRR {Num(overall.Mrr)}, behaviour {Num(overall.Behaviour)}, "
                + $"refusals {Num(overall.Refusals)}, median search {overall.MedianSearchMs:0} ms");
        }

        return results;
    }

    public static VariantAggregate Aggregate(IReadOnlyCollection<VariantCase> cases)
    {
        static double? Mean(IEnumerable<double?> values)
        {
            var present = values.Where(v => v is not null).Select(v => v!.Value).ToList();
            return present.Count == 0 ? null : Math.Round(present.Average(), 4);
        }

        static double? Median(IEnumerable<int> values)
        {
            var sorted = values.Select(v => (double)v).Order().ToList();
            return sorted.Count == 0 ? null
                : sorted.Count % 2 == 1 ? sorted[sorted.Count / 2]
                : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2;
        }

        var refusals = cases.Where(c => c.ExpectedBehaviour == "refusal").ToList();
        return new VariantAggregate(
            cases.Count,
            Mean(cases.Select(c => c.Metrics.RecallAt1)),
            Mean(cases.Select(c => c.Metrics.RecallAt3)),
            Mean(cases.Select(c => c.Metrics.RecallAt5)),
            Mean(cases.Select(c => c.Metrics.RecallAtTopK)),
            Mean(cases.Select(c => c.Metrics.ReciprocalRank)),
            cases.Count == 0 ? null : Math.Round(cases.Count(c => c.BehaviourCorrect) / (double)cases.Count, 4),
            refusals.Count == 0 ? null : Math.Round(refusals.Count(c => c.BehaviourCorrect) / (double)refusals.Count, 4),
            Median(cases.Select(c => c.SearchMs)),
            Median(cases.Where(c => c.RerankMs is not null).Select(c => c.RerankMs!.Value)),
            cases.Count(c => c.Rerank == "skipped"));
    }

    private static Dictionary<string, VariantAggregate> Group(IEnumerable<VariantCase> cases, Func<VariantCase, string> key) =>
        cases.GroupBy(key).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => Aggregate([.. g]));

    private static string Num(double? value) => value is { } v ? v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "—";
}
