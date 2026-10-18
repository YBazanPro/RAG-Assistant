using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;
using Microsoft.Extensions.Options;
using OllamaSharp;

namespace RagAssistant.Evaluation.Run;

/// <summary>
/// <c>judge &lt;report&gt;</c> (spec 018, AC-28): scores the stored answers of a run with a local judge model through the AppHost's
/// Ollama, after the run, so only one large model is loaded at a time. Groundedness and relevance come from
/// <c>Microsoft.Extensions.AI.Evaluation</c>; a requested language the detector does not know (the Spanish case) is asked of the
/// judge directly. The result is written next to the report as <c>judge-&lt;model&gt;.json</c> and <c>.md</c>; the report itself is
/// never changed.
/// </summary>
internal sealed class JudgeCommand(IOptions<EvaluationOptions> options, TimeProvider clock)
{
    private EvaluationOptions Settings => options.Value;

    public async Task<int> RunAsync(string reportPath, TextWriter output, CancellationToken cancellationToken)
    {
        var report = ReportWriter.Read(reportPath);
        var judgeModel = Settings.JudgeModel;
        if (string.Equals(judgeModel, report.Configuration.ChatModel, StringComparison.OrdinalIgnoreCase))
        {
            throw new HarnessException($"The judge model must not be the model under test ({judgeModel}); set Evaluation:JudgeModel to another model.");
        }

        if (!report.AnswerMetricsMeasured)
        {
            throw new HarnessException("This run did not measure answers (demo mode); there is nothing to judge.");
        }

        var passages = report.Passages ?? throw new HarnessException("This report has no corpus passages; run 'run' again.");
        using var ollama = new OllamaApiClient(Settings.OllamaUrl, judgeModel);
        await EnsurePulledAsync(ollama, judgeModel, output, cancellationToken);
        IChatClient judge = ollama;
        var configuration = new ChatConfiguration(judge);
        var groundedness = new GroundednessEvaluator();
        var relevance = new RelevanceEvaluator();

        var judged = new List<JudgedCase>();
        foreach (var c in report.Cases.Where(c => c.Status is "complete" or "partial" && c.AnswerText.Length > 0))
        {
            var messages = c.Turns.Select(t => new ChatMessage(ChatRole.User, t)).ToList();
            var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, c.AnswerText));
            var context = string.Join(
                "\n\n",
                c.Retrieved.Select(r => passages.TryGetValue(r.PassageId, out var p) ? $"[{r.Rank}] {p.Text}" : null).Where(t => t is not null));
            try
            {
                var grounded = await groundedness.EvaluateAsync(messages, response, configuration, [new GroundednessEvaluatorContext(context)], cancellationToken);
                var relevant = await relevance.EvaluateAsync(messages, response, configuration, null, cancellationToken);
                bool? language = c.ExpectedAnswerLanguage is "en" or "fr" ? null : await JudgeLanguageAsync(judge, c.AnswerText, c.ExpectedAnswerLanguage, cancellationToken);
                judged.Add(new JudgedCase(
                    c.Id, c.Category, c.QuestionLanguage,
                    grounded.Get<NumericMetric>(GroundednessEvaluator.GroundednessMetricName).Value,
                    relevant.Get<NumericMetric>(RelevanceEvaluator.RelevanceMetricName).Value,
                    language,
                    null));
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException && !cancellationToken.IsCancellationRequested)
            {
                // A failed judgement is recorded and the pass goes on, like a failed case in a run.
                judged.Add(new JudgedCase(c.Id, c.Category, c.QuestionLanguage, null, null, null, ex.Message));
            }

            var last = judged[^1];
            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"{c.Id,-10} groundedness {last.Groundedness?.ToString("0.0", CultureInfo.InvariantCulture) ?? "—"}  relevance {last.Relevance?.ToString("0.0", CultureInfo.InvariantCulture) ?? "—"}{(last.Error is null ? "" : $"  ({last.Error})")}"));
        }

        var result = new JudgeReport(
            report.RunId,
            report.Configuration.ChatModel,
            judgeModel,
            clock.GetUtcNow(),
            JudgeAggregate.Of(judged),
            judged.GroupBy(j => j.Category).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => JudgeAggregate.Of([.. g])),
            judged.GroupBy(j => j.QuestionLanguage).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => JudgeAggregate.Of([.. g])),
            judged);

        var folder = Directory.Exists(reportPath) ? reportPath : Path.GetDirectoryName(reportPath)!;
        var name = $"judge-{Slug(judgeModel)}";
        await File.WriteAllTextAsync(Path.Combine(folder, $"{name}.json"), JsonSerializer.Serialize(result, ReportWriter.Json), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(folder, $"{name}.md"), Markdown(result), cancellationToken);
        await output.WriteLineAsync($"Judged {judged.Count} answers with {judgeModel}: {Path.Combine(folder, name)}.md");
        return 0;
    }

    /// <summary>The judge model is pulled by the harness on demand, never by the AppHost (spec 018).</summary>
    private static async Task EnsurePulledAsync(OllamaApiClient ollama, string model, TextWriter output, CancellationToken cancellationToken)
    {
        IEnumerable<OllamaSharp.Models.Model> local;
        try
        {
            local = await ollama.ListLocalModelsAsync(cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new HarnessException($"Ollama is not reachable ({ex.Message}). Start the AppHost, or set Evaluation:OllamaUrl to its Ollama endpoint.");
        }

        if (local.Any(m => string.Equals(m.Name, model, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        await output.WriteLineAsync($"Pulling the judge model {model}...");
        await foreach (var status in ollama.PullModelAsync(model, cancellationToken))
        {
            if (status?.Status is { } text && status.Percent is var percent)
            {
                await output.WriteAsync(string.Create(CultureInfo.InvariantCulture, $"\r{text} {percent:0}%   "));
            }
        }

        await output.WriteLineAsync();
    }

    private static async Task<bool> JudgeLanguageAsync(IChatClient judge, string answer, string expected, CancellationToken cancellationToken)
    {
        var reply = await judge.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, "You identify languages. Reply with the two-letter ISO 639-1 code of the language the text is written in, and nothing else."),
                new ChatMessage(ChatRole.User, answer),
            ],
            new ChatOptions { MaxOutputTokens = 5, Temperature = 0 },
            cancellationToken);
        return reply.Text.Trim().Trim('.').Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string Markdown(JudgeReport report)
    {
        var md = new StringBuilder();
        md.AppendLine(CultureInfo.InvariantCulture, $"# Judgement of {report.RunId}");
        md.AppendLine();
        md.AppendLine(CultureInfo.InvariantCulture, $"Model under test: {report.ChatModel}; judge: {report.JudgeModel}; {report.JudgedAt:u}. Scores are 1 to 5.");
        md.AppendLine();
        md.AppendLine("| Group | Answers | Groundedness | Relevance | Requested language | Failed |");
        md.AppendLine("|---|---|---|---|---|---|");
        foreach (var (name, a) in new[] { ("all", report.Overall) }
            .Concat(report.ByCategory.Select(kv => (kv.Key, kv.Value)))
            .Concat(report.ByQuestionLanguage.Select(kv => ($"lang {kv.Key}", kv.Value))))
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"| {name} | {a.Answers} | {Num(a.Groundedness)} | {Num(a.Relevance)} | {Num(a.RequestedLanguageCorrectness)} | {a.Failed} |");
        }

        md.AppendLine();
        md.AppendLine("| Case | Groundedness | Relevance | Requested language |");
        md.AppendLine("|---|---|---|---|");
        foreach (var c in report.Cases)
        {
            md.AppendLine(CultureInfo.InvariantCulture,
                $"| `{c.Id}` | {Num(c.Groundedness)} | {Num(c.Relevance)} | {(c.RequestedLanguageCorrect is { } ok ? ok ? "✓" : "✗" : "—")}{(c.Error is null ? "" : " ⚠")} |");
        }

        return md.ToString();
    }

    private static string Num(double? value) => value is { } v ? v.ToString("0.00", CultureInfo.InvariantCulture) : "—";

    private static string Slug(string text) =>
        string.Concat(text.Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToLower(c, CultureInfo.InvariantCulture) : '-')).Trim('-');
}

internal sealed record JudgeReport(
    string RunId,
    string? ChatModel,
    string JudgeModel,
    DateTimeOffset JudgedAt,
    JudgeAggregate Overall,
    IReadOnlyDictionary<string, JudgeAggregate> ByCategory,
    IReadOnlyDictionary<string, JudgeAggregate> ByQuestionLanguage,
    IReadOnlyList<JudgedCase> Cases);

/// <param name="RequestedLanguageCorrect">Only for a requested language the detector does not know; null otherwise.</param>
internal sealed record JudgedCase(
    string Id, string Category, string QuestionLanguage, double? Groundedness, double? Relevance, bool? RequestedLanguageCorrect, string? Error);

internal sealed record JudgeAggregate(int Answers, int Failed, double? Groundedness, double? Relevance, double? RequestedLanguageCorrectness)
{
    public static JudgeAggregate Of(IReadOnlyCollection<JudgedCase> cases)
    {
        static double? Mean(IEnumerable<double?> values)
        {
            var present = values.Where(v => v is not null).Select(v => v!.Value).ToList();
            return present.Count == 0 ? null : Math.Round(present.Average(), 3);
        }

        return new JudgeAggregate(
            cases.Count,
            cases.Count(c => c.Error is not null),
            Mean(cases.Select(c => c.Groundedness)),
            Mean(cases.Select(c => c.Relevance)),
            Mean(cases.Select(c => c.RequestedLanguageCorrect is { } ok ? ok ? 1.0 : 0.0 : (double?)null)));
    }
}
