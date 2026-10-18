using RagAssistant.SharedKernel.Common.Text;

namespace RagAssistant.Evaluation.Run;

/// <summary>The deterministic metrics of spec 018: retrieval recall@k and MRR, behaviour, citations and answer language.</summary>
internal static class Scoring
{
    /// <summary>Answers are short like questions; the question minimum (spec 018, clarification 1) applies.</summary>
    private const int AnswerMinWords = 3;

    private static readonly Dictionary<string, string> ExpectedStatus = new(StringComparer.Ordinal)
    {
        ["answer"] = "complete",
        ["partial"] = "partial",
        ["refusal"] = "refused",
    };

    public static CaseMetrics Score(
        string expectedBehaviour,
        string expectedLanguage,
        int expectedEvidence,
        string status,
        IReadOnlyList<RetrievedPassage> retrieved,
        IReadOnlyCollection<Guid> cited,
        string answerText,
        int topK,
        bool answerMetrics)
    {
        double? Recall(int k) => expectedEvidence == 0
            ? null
            : (double)retrieved.Take(k).Where(p => p.Matches is not null).Select(p => p.Matches).Distinct().Count() / expectedEvidence;

        double? reciprocal = expectedEvidence == 0
            ? null
            : retrieved.FirstOrDefault(p => p.Matches is not null) is { } first ? 1.0 / first.Rank : 0;

        if (!answerMetrics || status is "error")
        {
            return new CaseMetrics(Recall(1), Recall(3), Recall(5), Recall(topK), reciprocal, null, null, null, null, null);
        }

        var behaviour = ExpectedStatus.TryGetValue(expectedBehaviour, out var wanted) && status == wanted;
        var answered = status is "complete" or "partial";

        double? citationRecall = null, citationPrecision = null;
        if (answered && expectedEvidence > 0)
        {
            var citedMatches = retrieved.Where(p => cited.Contains(p.PassageId) && p.Matches is not null).ToList();
            citationRecall = (double)citedMatches.Select(p => p.Matches).Distinct().Count() / expectedEvidence;
            citationPrecision = cited.Count == 0 ? 0 : (double)citedMatches.Count / cited.Count;
        }

        string? detected = null;
        bool? languageCorrect = null;
        if (answered)
        {
            var detection = LanguageDetector.Detect(answerText, AnswerMinWords);
            detected = detection.IsConfident ? detection.Language : null;
            // Only English and French are detected; another requested language is left to the judge pass. An answer too short to
            // classify ("Skylane Travel.") is not measured rather than counted as wrong.
            if (detected is not null && expectedLanguage is LanguageDetector.English or LanguageDetector.French)
            {
                languageCorrect = detected == expectedLanguage;
            }
        }

        return new CaseMetrics(Recall(1), Recall(3), Recall(5), Recall(topK), reciprocal, behaviour, citationRecall, citationPrecision, detected, languageCorrect);
    }

    public static Aggregate Aggregate(IReadOnlyCollection<CaseResult> cases)
    {
        static double? Mean(IEnumerable<double?> values)
        {
            var present = values.Where(v => v is not null).Select(v => v!.Value).ToList();
            return present.Count == 0 ? null : Math.Round(present.Average(), 4);
        }

        static double? Rate(IEnumerable<bool?> values) => Mean(values.Select(v => v is null ? (double?)null : v.Value ? 1 : 0));

        var firstTokens = cases.Where(c => c.FirstTokenMs is not null).Select(c => (double)c.FirstTokenMs!.Value).Order().ToList();
        double? median = firstTokens.Count == 0 ? null
            : firstTokens.Count % 2 == 1 ? firstTokens[firstTokens.Count / 2]
            : (firstTokens[(firstTokens.Count / 2) - 1] + firstTokens[firstTokens.Count / 2]) / 2;

        return new Aggregate(
            cases.Count,
            cases.Count(c => c.Error is not null),
            Mean(cases.Select(c => c.Metrics.RecallAt1)),
            Mean(cases.Select(c => c.Metrics.RecallAt3)),
            Mean(cases.Select(c => c.Metrics.RecallAt5)),
            Mean(cases.Select(c => c.Metrics.RecallAtTopK)),
            Mean(cases.Select(c => c.Metrics.ReciprocalRank)),
            Rate(cases.Select(c => c.Metrics.BehaviourCorrect)),
            Rate(cases.Where(c => c.ExpectedBehaviour == "refusal").Select(c => c.Metrics.BehaviourCorrect)),
            Mean(cases.Select(c => c.Metrics.CitationRecall)),
            Mean(cases.Select(c => c.Metrics.CitationPrecision)),
            Rate(cases.Select(c => c.Metrics.AnswerLanguageCorrect)),
            median);
    }
}
