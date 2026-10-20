using System.Globalization;
using System.Text;

namespace RagAssistant.Evaluation.Run;

/// <summary>
/// <c>sweep &lt;report&gt;</c> (spec 018, AC-30): replays the evidence rules of spec 011 (floor, near-duplicate reduction, cut,
/// outcome) over the raw candidate pools stored in a report, for a grid of <c>MinScore</c>, <c>ConfidentScore</c> and
/// <c>DuplicateOverlap</c>. No model is called: behaviour is what the retrieval outcome alone predicts (no evidence → refusal,
/// low confidence → partial, sufficient → answer), so a setting is judged on what retrieval hands the model.
/// </summary>
internal static class SweepCommand
{
    public static (string Markdown, Setting Best) Sweep(RunReport report)
    {
        var passages = report.Passages
            ?? throw new HarnessException("This report has no corpus passages (written before the sweep existed); run 'run' again.");
        var cases = report.Cases.Where(c => c.Error is null && c.Pool.Count > 0 || c.ExpectedBehaviour == "refusal").ToList();
        if (cases.Count == 0)
        {
            throw new HarnessException("The report has no candidate pools to sweep.");
        }

        var shingles = passages.ToDictionary(p => p.Key, p => Shingles(p.Value.Text));
        var topK = report.Configuration.TopK;
        // Spec 019: when the run reranked, the pools carry rerank scores and the rerank thresholds are the ones to calibrate.
        var reranked = report.Configuration.Reranking == true && report.Configuration.RerankMinScore is not null;
        var current = reranked
            ? new Thresholds(report.Configuration.RerankMinScore!.Value, report.Configuration.RerankConfidentScore!.Value, report.Configuration.DuplicateOverlap)
            : new Thresholds(report.Configuration.MinScore, report.Configuration.ConfidentScore, report.Configuration.DuplicateOverlap);

        var results = new List<Setting>();
        foreach (var thresholds in reranked ? RerankGrid(current) : Grid(current))
        {
            results.Add(Evaluate(thresholds, cases, shingles, topK));
        }

        var baseline = Evaluate(current, cases, shingles, topK);
        // Refusals are few, so overall behaviour alone would happily trade them for answers; the product's first principle is an
        // honest refusal (spec 012), so a recommendation never refuses less well than the current setting. Then behaviour, then
        // recall; on a tie the setting closest to the current one wins, so the sweep never moves for nothing.
        var best = results
            .Where(s => (s.Refusals ?? 1) >= (baseline.Refusals ?? 1))
            .Append(baseline)
            .OrderByDescending(s => s.Behaviour)
            .ThenByDescending(s => s.RecallAtTopK)
            .ThenBy(s => Distance(s.Thresholds, current))
            .First();
        return (Markdown(report, results, baseline, best, cases.Count), best);
    }

    private static Setting Evaluate(Thresholds t, List<CaseResult> cases, Dictionary<Guid, HashSet<string>> shingles, int topK)
    {
        var correct = 0;
        var refusalsCorrect = 0;
        var refusals = 0;
        var recall = new List<double>();
        foreach (var c in cases)
        {
            var (outcome, kept) = Apply(c.Pool, t, shingles, topK);
            var predicted = outcome switch
            {
                Outcome.NoEvidence => "refusal",
                Outcome.LowConfidence => "partial",
                _ => "answer",
            };
            if (predicted == c.ExpectedBehaviour)
            {
                correct++;
            }

            if (c.ExpectedBehaviour == "refusal")
            {
                refusals++;
                refusalsCorrect += predicted == "refusal" ? 1 : 0;
            }

            if (c.ExpectedEvidence > 0)
            {
                recall.Add((double)kept.Where(k => k.Matches is not null).Select(k => k.Matches).Distinct().Count() / c.ExpectedEvidence);
            }
        }

        return new Setting(
            t,
            Math.Round((double)correct / cases.Count, 4),
            refusals == 0 ? null : Math.Round((double)refusalsCorrect / refusals, 4),
            recall.Count == 0 ? 0 : Math.Round(recall.Average(), 4));
    }

    /// <summary>The same steps as <c>EvidenceRules.Apply</c> in the Documents module, on a pool already in rank order.</summary>
    private static (Outcome Outcome, List<PoolCandidate> Kept) Apply(
        IReadOnlyList<PoolCandidate> pool, Thresholds t, Dictionary<Guid, HashSet<string>> shingles, int topK)
    {
        var kept = new List<(PoolCandidate Candidate, HashSet<string> Shingles)>();
        foreach (var candidate in pool)
        {
            if (candidate.Score < t.MinScore)
            {
                continue;
            }

            if (kept.Count == topK)
            {
                break;
            }

            var own = shingles.GetValueOrDefault(candidate.PassageId) ?? [];
            if (!kept.Any(k => Jaccard(own, k.Shingles) >= t.DuplicateOverlap))
            {
                kept.Add((candidate, own));
            }
        }

        // The best kept score, as EvidenceRules does since spec 019: in hybrid order the first passage need not score best.
        var outcome = kept.Count == 0 ? Outcome.NoEvidence
            : kept.Max(k => k.Candidate.Score) < t.ConfidentScore ? Outcome.LowConfidence
            : Outcome.Sufficient;
        return (outcome, [.. kept.Select(k => k.Candidate)]);
    }

    private static IEnumerable<Thresholds> Grid(Thresholds current)
    {
        double[] overlaps = [.. new[] { 0.6, 0.7, 0.8, 0.9, current.DuplicateOverlap }.Distinct()];
        for (var min = 0.40; min <= 0.7001; min += 0.025)
        {
            for (var confident = min; confident <= 0.8001; confident += 0.025)
            {
                foreach (var overlap in overlaps)
                {
                    yield return new Thresholds(Math.Round(min, 3), Math.Round(confident, 3), overlap);
                }
            }
        }
    }

    /// <summary>Rerank scores are sigmoid probabilities that spread over the whole of 0..1, unlike cosines (spec 019).</summary>
    private static IEnumerable<Thresholds> RerankGrid(Thresholds current)
    {
        double[] overlaps = [.. new[] { 0.7, 0.8, 0.9, current.DuplicateOverlap }.Distinct()];
        for (var min = 0.0; min <= 0.6001; min += 0.05)
        {
            for (var confident = min; confident <= 0.9501; confident += 0.05)
            {
                foreach (var overlap in overlaps)
                {
                    yield return new Thresholds(Math.Round(min, 3), Math.Round(confident, 3), overlap);
                }
            }
        }
    }

    private static double Distance(Thresholds a, Thresholds b) =>
        Math.Abs(a.MinScore - b.MinScore) + Math.Abs(a.ConfidentScore - b.ConfidentScore) + Math.Abs(a.DuplicateOverlap - b.DuplicateOverlap);

    private static string Markdown(RunReport report, List<Setting> results, Setting baseline, Setting best, int caseCount)
    {
        var md = new StringBuilder();
        md.AppendLine(CultureInfo.InvariantCulture, $"# Threshold sweep of {report.RunId}");
        md.AppendLine();
        md.AppendLine(CultureInfo.InvariantCulture,
            $"{results.Count} settings over {caseCount} cases (topK {report.Configuration.TopK}). Behaviour is predicted from the retrieval outcome only; no model was called. The best setting refuses at least as well as the current one.");
        md.AppendLine();
        md.AppendLine("| | MinScore | ConfidentScore | DuplicateOverlap | Behaviour | Refusals | R@topK |");
        md.AppendLine("|---|---|---|---|---|---|---|");
        md.AppendLine(Row("current", baseline));
        md.AppendLine(Row("**best**", best));
        md.AppendLine();
        md.AppendLine("## All settings, best first");
        md.AppendLine();
        md.AppendLine("| MinScore | ConfidentScore | DuplicateOverlap | Behaviour | Refusals | R@topK |");
        md.AppendLine("|---|---|---|---|---|---|");
        foreach (var s in results.OrderByDescending(s => s.Behaviour).ThenByDescending(s => s.RecallAtTopK))
        {
            md.AppendLine(Row(null, s));
        }

        return md.ToString();
    }

    private static string Row(string? label, Setting s) => string.Create(CultureInfo.InvariantCulture,
        $"|{(label is null ? "" : $" {label} |")} {s.Thresholds.MinScore:0.000} | {s.Thresholds.ConfidentScore:0.000} | {s.Thresholds.DuplicateOverlap:0.0} | {s.Behaviour:0.00} | {(s.Refusals is { } r ? r.ToString("0.00", CultureInfo.InvariantCulture) : "—")} | {s.RecallAtTopK:0.00} |");

    // Word 3-gram Jaccard, as NearDuplicates in the Documents module (spec 011); fewer than 3 words form one shingle.
    private static HashSet<string> Shingles(string text)
    {
        var words = new List<string>();
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var inWord = i < text.Length && char.IsLetterOrDigit(text[i]);
            if (inWord && start < 0)
            {
                start = i;
            }
            else if (!inWord && start >= 0)
            {
                words.Add(text[start..i].ToLowerInvariant());
                start = -1;
            }
        }

        if (words.Count < 3)
        {
            return words.Count == 0 ? [] : [string.Join(' ', words)];
        }

        var set = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i <= words.Count - 3; i++)
        {
            set.Add(string.Join(' ', words[i], words[i + 1], words[i + 2]));
        }

        return set;
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }

        var shared = a.Count(b.Contains);
        return (double)shared / (a.Count + b.Count - shared);
    }

    private enum Outcome
    {
        NoEvidence,
        LowConfidence,
        Sufficient,
    }
}

internal sealed record Thresholds(double MinScore, double ConfidentScore, double DuplicateOverlap);

internal sealed record Setting(Thresholds Thresholds, double Behaviour, double? Refusals, double RecallAtTopK);
