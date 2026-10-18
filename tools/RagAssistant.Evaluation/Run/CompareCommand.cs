using System.Globalization;
using System.Text;
using System.Text.Json;

namespace RagAssistant.Evaluation.Run;

/// <summary>
/// <c>compare &lt;reportA&gt; &lt;reportB&gt;</c> (spec 018, AC-29): every metric of every group side by side with B − A, and the
/// configuration keys that differ flagged first, since they explain the deltas.
/// </summary>
internal static class CompareCommand
{
    private static readonly (string Name, Func<Aggregate, double?> Value)[] Metrics =
    [
        ("R@1", a => a.RecallAt1),
        ("R@3", a => a.RecallAt3),
        ("R@5", a => a.RecallAt5),
        ("R@topK", a => a.RecallAtTopK),
        ("MRR", a => a.Mrr),
        ("Behaviour", a => a.BehaviourCorrectness),
        ("Refusals", a => a.RefusalCorrectness),
        ("Citation recall", a => a.CitationRecall),
        ("Citation precision", a => a.CitationPrecision),
        ("Answer language", a => a.AnswerLanguageCorrectness),
        ("Median first token (ms)", a => a.MedianFirstTokenMs),
        ("Errors", a => a.Errors),
    ];

    public static string Compare(RunReport a, RunReport b)
    {
        var md = new StringBuilder();
        md.AppendLine(CultureInfo.InvariantCulture, $"# Comparison: A = {a.RunId}, B = {b.RunId}");
        md.AppendLine();

        var differences = ConfigurationDifferences(a.Configuration, b.Configuration).ToList();
        if (differences.Count == 0)
        {
            md.AppendLine("Configuration: identical.");
        }
        else
        {
            md.AppendLine("## Configuration differences");
            md.AppendLine();
            md.AppendLine("| Setting | A | B |");
            md.AppendLine("|---|---|---|");
            foreach (var (key, va, vb) in differences)
            {
                md.AppendLine(CultureInfo.InvariantCulture, $"| ⚠ {key} | {va} | {vb} |");
            }
        }

        foreach (var (label, report) in new[] { ("A", a), ("B", b) })
        {
            if (!report.Valid)
            {
                md.AppendLine(CultureInfo.InvariantCulture, $"> Run {label} is invalid: {report.InvalidReason}");
            }

            if (!report.AnswerMetricsMeasured)
            {
                md.AppendLine(CultureInfo.InvariantCulture, $"> Run {label} did not measure answer metrics (demo mode).");
            }
        }

        var groups = new List<(string Name, Aggregate? A, Aggregate? B)> { ("all", a.Overall, b.Overall) };
        groups.AddRange(Keys(a.ByCategory, b.ByCategory).Select(k => (k, a.ByCategory.GetValueOrDefault(k), b.ByCategory.GetValueOrDefault(k))));
        groups.AddRange(Keys(a.ByQuestionLanguage, b.ByQuestionLanguage)
            .Select(k => ($"lang {k}", a.ByQuestionLanguage.GetValueOrDefault(k), b.ByQuestionLanguage.GetValueOrDefault(k))));

        foreach (var (name, ga, gb) in groups)
        {
            md.AppendLine();
            md.AppendLine(CultureInfo.InvariantCulture, $"## {name} ({ga?.Cases ?? 0} → {gb?.Cases ?? 0} cases)");
            md.AppendLine();
            md.AppendLine("| Metric | A | B | B − A |");
            md.AppendLine("|---|---|---|---|");
            foreach (var (metric, value) in Metrics)
            {
                var va = ga is null ? null : value(ga);
                var vb = gb is null ? null : value(gb);
                md.AppendLine(CultureInfo.InvariantCulture, $"| {metric} | {Num(va)} | {Num(vb)} | {Delta(va, vb)} |");
            }
        }

        return md.ToString();
    }

    /// <summary>Every configuration property whose value differs, compared as JSON so new properties need no code here.</summary>
    private static IEnumerable<(string Key, string A, string B)> ConfigurationDifferences(RunConfiguration a, RunConfiguration b)
    {
        var ja = JsonSerializer.SerializeToElement(a, ReportWriter.Json);
        var jb = JsonSerializer.SerializeToElement(b, ReportWriter.Json);
        foreach (var property in ja.EnumerateObject())
        {
            var other = jb.TryGetProperty(property.Name, out var value) ? value.ToString() : "—";
            if (property.Value.ToString() != other)
            {
                yield return (property.Name, property.Value.ToString(), other);
            }
        }
    }

    private static IEnumerable<string> Keys(IReadOnlyDictionary<string, Aggregate> a, IReadOnlyDictionary<string, Aggregate> b) =>
        a.Keys.Union(b.Keys).Order(StringComparer.Ordinal);

    private static string Num(double? value) => value is { } v ? v.ToString(v >= 10 ? "0" : "0.00", CultureInfo.InvariantCulture) : "—";

    private static string Delta(double? a, double? b) => (a, b) switch
    {
        ({ } x, { } y) => (y - x).ToString(Math.Abs(y - x) >= 10 ? "+0;-0;0" : "+0.00;-0.00;0.00", CultureInfo.InvariantCulture),
        _ => "—",
    };
}
