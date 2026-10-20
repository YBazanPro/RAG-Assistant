using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace RagAssistant.Evaluation.Run;

/// <summary>
/// Writes a run as <c>report.json</c> (machine) and <c>report.md</c> (human) into <c>.data/evaluation/&lt;run id&gt;/</c>, which is
/// git-ignored (spec 018).
/// </summary>
internal sealed class ReportWriter(IOptions<EvaluationOptions> options)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public async Task<string> WriteAsync(RunReport report, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(RepositoryPaths.Resolve(options.Value.ReportsPath), report.RunId);
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "report.json"), JsonSerializer.Serialize(report, Json), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(folder, "report.md"), Markdown(report), cancellationToken);
        return folder;
    }

    public static RunReport Read(string path)
    {
        var file = Directory.Exists(path) ? Path.Combine(path, "report.json") : path;
        if (!File.Exists(file))
        {
            throw new HarnessException($"No report at {file}.");
        }

        return JsonSerializer.Deserialize<RunReport>(File.ReadAllText(file), Json) ?? throw new HarnessException($"{file} is empty.");
    }

    private static string Markdown(RunReport report)
    {
        var c = report.Configuration;
        var md = new StringBuilder();
        md.AppendLine(CultureInfo.InvariantCulture, $"# Evaluation run {report.RunId}");
        md.AppendLine();
        if (!report.Valid)
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"> **Invalid run:** {report.InvalidReason}");
            md.AppendLine();
        }

        if (!report.AnswerMetricsMeasured)
        {
            md.AppendLine("> Demo mode: answer metrics are not measured.");
            md.AppendLine();
        }

        md.AppendLine("| Setting | Value |");
        md.AppendLine("|---|---|");
        foreach (var (name, value) in new (string, object?)[]
        {
            ("Started", report.StartedAt.ToString("u", CultureInfo.InvariantCulture)),
            ("Chat", $"{c.ChatProvider} · {c.ChatModel ?? "—"}"),
            ("Embedding model", c.EmbeddingModel),
            ("Prompt versions", $"{c.PromptVersion} / {c.RewritePromptVersion}"),
            ("Retrieval", $"topK {c.TopK} · MinScore {c.MinScore} · ConfidentScore {c.ConfidentScore} · DuplicateOverlap {c.DuplicateOverlap}"),
            ("Chunking", $"{c.ChunkMaxTokens} tokens, overlap {c.ChunkOverlapTokens}"),
            ("Stages", Stages(c)),
            ("Scope", $"tag {c.Scope}"),
            ("Corpus / dataset", $"{c.CorpusHash[..12]} / {c.DatasetHash[..12]}"),
            ("Commit", c.GitCommit ?? "—"),
            ("Cases", $"{report.Cases.Count} run, {report.BrokenCases.Count} broken"),
        })
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"| {name} | {value} |");
        }

        md.AppendLine();
        md.AppendLine("## Metrics");
        md.AppendLine();
        AppendTable(md, [("all", report.Overall), .. report.ByCategory.Select(kv => (kv.Key, kv.Value)), .. report.ByQuestionLanguage.Select(kv => ($"lang {kv.Key}", kv.Value))]);

        if (report.Variants is { Count: > 0 } variants)
        {
            md.AppendLine();
            md.AppendLine("## Retrieval variants");
            md.AppendLine();
            md.AppendLine("Retrieval only, on the same queries; behaviour is what the retrieval outcome predicts (spec 019, run --variants).");
            md.AppendLine();
            md.AppendLine("| Variant | Group | Cases | R@1 | R@3 | R@5 | MRR | Behaviour | Refusals | Median search | Median rerank | Rerank skipped |");
            md.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
            foreach (var v in variants)
            {
                foreach (var (group, a) in new[] { ("all", v.Overall) }.Concat(v.ByQuestionLanguage.Select(kv => ($"lang {kv.Key}", kv.Value))).Concat(v.ByCategory.Select(kv => (kv.Key, kv.Value))))
                {
                    md.AppendLine(CultureInfo.InvariantCulture,
                        $"| {v.Name} | {group} | {a.Cases} | {Num(a.RecallAt1)} | {Num(a.RecallAt3)} | {Num(a.RecallAt5)} | {Num(a.Mrr)} | {Num(a.Behaviour)} | {Num(a.Refusals)} | {Ms(a.MedianSearchMs)} | {Ms(a.MedianRerankMs)} | {a.RerankSkipped} |");
                }
            }
        }

        if (report.BrokenCases.Count > 0)
        {
            md.AppendLine();
            md.AppendLine("## Broken cases (excluded)");
            md.AppendLine();
            foreach (var broken in report.BrokenCases)
            {
                md.AppendLine(CultureInfo.InvariantCulture, $"- `{broken.Id}`: {broken.Reason}");
            }
        }

        md.AppendLine();
        md.AppendLine("## Cases");
        md.AppendLine();
        md.AppendLine("| Case | Expected | Status | Behaviour | MRR | Citations R/P | Language | Injection | First token |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var r in report.Cases)
        {
            var m = r.Metrics;
            md.AppendLine(CultureInfo.InvariantCulture,
                $"| `{r.Id}` | {r.ExpectedBehaviour} | {r.Status}{(r.Error is null ? "" : " ⚠")} | {Mark(m.BehaviourCorrect)} | {Num(m.ReciprocalRank)} | {Num(m.CitationRecall)} / {Num(m.CitationPrecision)} | {r.ExpectedAnswerLanguage}→{m.AnswerLanguageDetected ?? "?"} {Mark(m.AnswerLanguageCorrect)} | {Mark(m.InjectionResisted)} | {(r.FirstTokenMs is { } ms ? $"{ms} ms" : "—")} |");
        }

        return md.ToString();
    }

    private static void AppendTable(StringBuilder md, IEnumerable<(string Name, Aggregate Value)> rows)
    {
        md.AppendLine("| Group | Cases | Errors | R@1 | R@3 | R@5 | R@topK | MRR | Behaviour | Refusals | Cit. recall | Cit. precision | Answer language | Injection resisted | Median first token |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var (name, a) in rows)
        {
            md.AppendLine(CultureInfo.InvariantCulture,
                $"| {name} | {a.Cases} | {a.Errors} | {Num(a.RecallAt1)} | {Num(a.RecallAt3)} | {Num(a.RecallAt5)} | {Num(a.RecallAtTopK)} | {Num(a.Mrr)} | {Num(a.BehaviourCorrectness)} | {Num(a.RefusalCorrectness)} | {Num(a.CitationRecall)} | {Num(a.CitationPrecision)} | {Num(a.AnswerLanguageCorrectness)} | {Num(a.InjectionResistance)} | {(a.MedianFirstTokenMs is { } ms ? $"{ms:0} ms" : "—")} |");
        }
    }

    /// <summary>Spec 019: the retrieval stages of the configuration; older reports have none.</summary>
    private static string Stages(RunConfiguration c) => c.RetrievalMode is null ? "—"
        : FormattableString.Invariant(
            $"{c.RetrievalMode} (RrfK {c.RrfK}{(c.MinKeywordCoverage is { } coverage ? FormattableString.Invariant($", MinKeywordCoverage {coverage}") : "")}) · reranking {(c.Reranking == true ? $"on, {c.RerankerModel ?? "no reranker configured"}, {c.RerankCandidates} candidates, MinScore {c.RerankMinScore}, ConfidentScore {c.RerankConfidentScore}" : "off")}");

    private static string Ms(double? value) => value is { } ms ? FormattableString.Invariant($"{ms:0} ms") : "—";

    private static string Num(double? value) => value is { } v ? v.ToString("0.00", CultureInfo.InvariantCulture) : "—";

    private static string Mark(bool? value) => value switch
    {
        true => "✓",
        false => "✗",
        null => "—",
    };
}
