using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RagAssistant.Evaluation.Api;
using RagAssistant.Evaluation.Commands;
using RagAssistant.Evaluation.Corpus;
using RagAssistant.Evaluation.Golden;

namespace RagAssistant.Evaluation.Run;

/// <summary>
/// <c>run</c> (spec 018): seeds, checks the dataset against the seeded passages, asks every golden case in its own conversation
/// scoped to the sample tag, scores it and writes a report. One case failing never stops the run.
/// </summary>
internal sealed class RunCommand(
    SeedCommand seed,
    RagAssistantApi api,
    ReportWriter reports,
    IOptions<EvaluationOptions> options,
    TimeProvider clock)
{
    private EvaluationOptions Settings => options.Value;

    public async Task<int> RunAsync(TextWriter output, CancellationToken cancellationToken)
    {
        var started = clock.GetUtcNow();
        await seed.RunAsync(output, cancellationToken);

        var corpusPath = RepositoryPaths.Resolve(Settings.CorpusPath);
        var manifest = CorpusManifest.Load(corpusPath);
        var (dataset, datasetHash) = GoldenDataset.Load(RepositoryPaths.Resolve(Settings.DatasetPath));
        var user = Settings.MaintainerUser;

        var passages = await LoadPassagesAsync(manifest, cancellationToken);
        var (runnable, broken) = Validate(dataset, passages);
        foreach (var b in broken)
        {
            await output.WriteLineAsync($"broken     {b.Id}: {b.Reason}");
        }

        var configuration = await ConfigurationAsync(user, CorpusHash(corpusPath, manifest), datasetHash, cancellationToken);
        var answerMetrics = configuration.ChatProvider != "Demo";
        if (!answerMetrics)
        {
            await output.WriteLineAsync("Demo mode: answer metrics are not measured (spec 018).");
        }

        var results = new List<CaseResult>();
        foreach (var golden in runnable)
        {
            var result = await RunCaseAsync(golden, passages, configuration.TopK, answerMetrics, user, cancellationToken);
            results.Add(result);
            await output.WriteLineAsync($"{result.Status,-10} {golden.Id}{(result.Error is null ? "" : $"  ({result.Error})")}");
        }

        var models = results.Select(r => r.ChatModel).Where(m => m is not null).Distinct(StringComparer.Ordinal).ToList();
        var invalid = models.Count > 1 ? $"The traces show several chat models: {string.Join(", ", models)}." : null;
        configuration = configuration with
        {
            ChatModel = models.Count == 1 ? models[0] : configuration.ChatModel,
            PromptVersion = results.Select(r => r.PromptVersion).FirstOrDefault(p => p is not null) ?? configuration.PromptVersion,
        };

        var report = new RunReport(
            RunReport.CurrentSchemaVersion,
            $"{started.UtcDateTime:yyyyMMdd-HHmmss}-{Slug(configuration.ChatModel ?? configuration.ChatProvider)}",
            started,
            clock.GetUtcNow(),
            invalid is null,
            invalid,
            answerMetrics,
            configuration,
            broken,
            Scoring.Aggregate(results),
            Group(results, r => r.Category),
            Group(results, r => r.QuestionLanguage),
            results,
            passages.All);
        var folder = await reports.WriteAsync(report, cancellationToken);
        await output.WriteLineAsync($"Report: {folder}");
        return invalid is null ? 0 : 3;
    }

    private async Task<CaseResult> RunCaseAsync(
        GoldenCase golden, CorpusPassages passages, int topK, bool answerMetrics, string user, CancellationToken cancellationToken)
    {
        var expectedLanguage = golden.Expected.AnswerLanguage ?? golden.QuestionLanguage;
        StreamedAnswer? last = null;
        string? error = null;
        try
        {
            var conversation = await api.CreateConversationAsync(user, Settings.SampleTag, cancellationToken);
            foreach (var turn in golden.Turns)
            {
                last = await api.AskAsync(user, conversation, turn, cancellationToken);
                if (last.RejectedWith is not null || last.Error is not null)
                {
                    error = last.RejectedWith ?? last.Error!.Code;
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            // Spec 018: a failed or timed-out case is recorded as an error and the run continues.
            error = ex.GetType().Name + ": " + ex.Message;
        }

        var retrieved = (last?.Retrieval?.Sources ?? [])
            .Select((s, i) => new RetrievedPassage(i + 1, s.PassageId, passages.DocumentOf(s.PassageId), s.Score, s.Language, passages.MatchOf(golden, s.PassageId)))
            .ToList();
        var cited = (last?.Completed?.Citations ?? []).Select(c => c.PassageId).ToList();

        RequestDetail? trace = null;
        IReadOnlyList<PoolCandidate> pool = [];
        if (last is { AssistantMessageId: var id } && id != Guid.Empty)
        {
            try
            {
                trace = await api.FindTraceAsync(user, id, golden.Turns[^1], cancellationToken);
                if (last.RetrievalQuery is { } query)
                {
                    var search = await api.SearchCandidatesAsync(user, query, Settings.SampleTag, cancellationToken);
                    pool = [.. (search.Candidates ?? []).Select(c => new PoolCandidate(
                        c.PassageId, passages.DocumentOf(c.PassageId), c.Score, c.Dropped, passages.MatchOf(golden, c.PassageId)))];
                }
            }
            catch (Exception ex) when (ex is HarnessException or HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // The answer is scored anyway; only its trace or its candidate pool is missing (the sweep then skips the case).
                error ??= "diagnostics unavailable: " + ex.Message;
            }
        }

        var status = error is not null && last?.Completed is null ? "error" : last?.Status ?? "error";
        var metrics = Scoring.Score(
            golden.Expected.Behaviour, expectedLanguage, golden.Expected.Evidence.Count, status, retrieved, cited, last?.Text ?? "", topK, answerMetrics);

        return new CaseResult(
            golden.Id, golden.Category, golden.QuestionLanguage, golden.Turns, golden.Expected.Behaviour, expectedLanguage,
            golden.Expected.Evidence.Count, status, last?.Completed?.RefusalReason, error, last?.RetrievalQuery, last?.Retrieval?.Outcome,
            last?.Text ?? "", retrieved, cited, pool, trace?.Generation.ChatModel, trace?.Generation.PromptVersion,
            trace?.Question.DetectedLanguage, trace?.Generation.AnswerLanguage, trace?.Generation.FirstTokenMs, trace?.Generation.TotalMs, metrics);
    }

    private async Task<CorpusPassages> LoadPassagesAsync(CorpusManifest manifest, CancellationToken cancellationToken)
    {
        var user = Settings.MaintainerUser;
        var found = await api.FindByTagAsync(user, Settings.SampleTag, cancellationToken);
        var passages = new Dictionary<Guid, (string Document, string Text)>();
        foreach (var document in manifest.Documents)
        {
            var match = found.SingleOrDefault(d => d.Title == document.Title)
                ?? throw new HarnessException($"'{document.Key}' is not in the app after seeding.");
            var detail = (await api.GetAsync(user, match.Id, cancellationToken)).Require($"read '{document.Key}'");
            var version = detail.LatestVersion ?? throw new HarnessException($"'{document.Key}' has no version.");
            foreach (var passage in await api.PassagesAsync(user, match.Id, version.Number, cancellationToken))
            {
                passages[passage.Id] = (document.Key, passage.Text);
            }
        }

        return new CorpusPassages(passages);
    }

    /// <summary>AC-25: a case whose quote is no longer in its document is listed and excluded, never silently passed.</summary>
    private static (List<GoldenCase> Runnable, List<BrokenCase> Broken) Validate(GoldenDataset dataset, CorpusPassages passages)
    {
        var runnable = new List<GoldenCase>();
        var broken = new List<BrokenCase>();
        foreach (var golden in dataset.Cases)
        {
            var missing = golden.Expected.Evidence.Where(e => !passages.Any(e)).ToList();
            if (missing.Count == 0)
            {
                runnable.Add(golden);
            }
            else
            {
                broken.Add(new BrokenCase(golden.Id, string.Join("; ", missing.Select(m => $"'{m.Quote}' not found in {m.Document}"))));
            }
        }

        return (runnable, broken);
    }

    private async Task<RunConfiguration> ConfigurationAsync(string user, string corpusHash, string datasetHash, CancellationToken cancellationToken)
    {
        var chat = await api.ChatConfigurationAsync(user, cancellationToken);
        var documents = await api.DocumentsConfigurationAsync(user, cancellationToken);
        return new RunConfiguration(
            chat.GetProperty("provider").GetString() ?? "unknown",
            chat.TryGetProperty("chatModel", out var model) ? model.GetString() : null,
            documents.GetProperty("embeddingModel").GetString(),
            chat.GetProperty("promptVersion").GetString(),
            chat.GetProperty("rewritePromptVersion").GetString(),
            chat.GetProperty("topK").GetInt32(),
            documents.GetProperty("minScore").GetDouble(),
            documents.GetProperty("confidentScore").GetDouble(),
            documents.GetProperty("duplicateOverlap").GetDouble(),
            documents.GetProperty("chunkMaxTokens").GetInt32(),
            documents.GetProperty("chunkOverlapTokens").GetInt32(),
            corpusHash,
            datasetHash,
            Settings.SampleTag,
            GitCommit(),
            Settings.ApiBaseUrl.ToString());
    }

    private static string CorpusHash(string corpusPath, CorpusManifest manifest)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(File.ReadAllBytes(Path.Combine(corpusPath, "manifest.json")));
        foreach (var file in manifest.Documents.SelectMany(d => d.Versions))
        {
            hash.AppendData(File.ReadAllBytes(Path.Combine(corpusPath, file)));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string? GitCommit()
    {
        try
        {
            using var git = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD")
            {
                WorkingDirectory = RepositoryPaths.Root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            var commit = git?.StandardOutput.ReadToEnd().Trim();
            git?.WaitForExit();
            return git?.ExitCode == 0 ? commit : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No git on the path: the report simply has no commit.
            return null;
        }
    }

    private static Dictionary<string, Aggregate> Group(IEnumerable<CaseResult> results, Func<CaseResult, string> key) =>
        results.GroupBy(key).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => Scoring.Aggregate([.. g]));

    private static string Slug(string text) =>
        string.Concat(text.Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToLower(c, CultureInfo.InvariantCulture) : '-')).Trim('-');
}

/// <summary>The passages of the seeded sample documents: their document key and text, for matching expected evidence.</summary>
internal sealed class CorpusPassages(Dictionary<Guid, (string Document, string Text)> passages)
{
    public IReadOnlyDictionary<Guid, CorpusPassage> All => passages.ToDictionary(p => p.Key, p => new CorpusPassage(p.Value.Document, p.Value.Text));

    public string? DocumentOf(Guid passageId) => passages.TryGetValue(passageId, out var p) ? p.Document : null;

    /// <summary>The index of the expected evidence item the passage matches, or null.</summary>
    public int? MatchOf(GoldenCase golden, Guid passageId)
    {
        if (!passages.TryGetValue(passageId, out var passage))
        {
            return null;
        }

        for (var i = 0; i < golden.Expected.Evidence.Count; i++)
        {
            var expected = golden.Expected.Evidence[i];
            if (expected.Document == passage.Document && Evidence.Contains(passage.Text, expected.Quote))
            {
                return i;
            }
        }

        return null;
    }

    public bool Any(ExpectedEvidence expected) =>
        passages.Values.Any(p => p.Document == expected.Document && Evidence.Contains(p.Text, expected.Quote));
}
