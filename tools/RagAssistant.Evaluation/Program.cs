using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RagAssistant.Evaluation;
using RagAssistant.Evaluation.Api;
using RagAssistant.Evaluation.Commands;
using RagAssistant.Evaluation.Corpus;
using RagAssistant.Evaluation.Run;

// The evaluation harness (spec 018). Commands run against a running AppHost; settings come from appsettings.json, user secrets,
// RAGEVAL_ environment variables and --Evaluation:Key=value arguments.
const string Usage = """
    Usage: dotnet run --project tools/RagAssistant.Evaluation -- <command> [--Evaluation:Key=value ...]

    Commands:
      seed      Import or update the sample corpus (samples/corpus) in the running app.
      run       Seed, ask every golden question (samples/golden) and write a report to .data/evaluation.
                run --variants also measures retrieval for vector, hybrid and hybrid + rerank on the same queries (spec 019).
      compare   compare <reportA> <reportB>: every metric side by side, configuration differences flagged.
      sweep     sweep <report>: replay retrieval thresholds over the report's candidate pools and recommend the best.
      judge     judge <report>: score the stored answers with Evaluation:JudgeModel (groundedness, relevance, requested language).
      render    Write the generated sample PDFs to .data/evaluation/pdf, to look at them.

    A report is a run folder or its report.json.
    """;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine(Usage);
    return 0;
}

var command = args[0];
// Positional arguments (report paths) come first; --Key=value settings follow.
string[] positional = [.. args[1..].TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal))];
string[] options = [.. args[1..].Skip(positional.Length)];
// The one flag (spec 019): run --variants also measures retrieval for vector, hybrid and hybrid + rerank.
var variants = options.Contains("--variants", StringComparer.Ordinal);
string[] settings = [.. options.Where(o => o != "--variants")];
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = settings,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Configuration.AddUserSecrets<EvaluationOptions>(optional: true);
builder.Configuration.AddEnvironmentVariables("RAGEVAL_");
builder.Configuration.AddCommandLine(settings);

builder.Services.AddOptions<EvaluationOptions>().Bind(builder.Configuration.GetSection(EvaluationOptions.Section));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient<DevTokens>();
builder.Services.AddHttpClient<RagAssistantApi>((services, http) =>
{
    http.BaseAddress = services.GetRequiredService<IOptions<EvaluationOptions>>().Value.ApiBaseUrl;
    // Large uploads and slow answers: the harness waits for the app, the app's own deadlines decide.
    http.Timeout = TimeSpan.FromMinutes(10);
});
builder.Services.AddTransient<SeedCommand>();
builder.Services.AddTransient<RunCommand>();
builder.Services.AddTransient<ReportWriter>();
builder.Services.AddTransient<JudgeCommand>();
// Loading a judge model into memory, then scoring a long answer, takes minutes on a laptop; the default 100 s is too short.
builder.Services.AddHttpClient(JudgeCommand.HttpClientName, http => http.Timeout = TimeSpan.FromMinutes(10))
    .AddHttpMessageHandler(services => new JudgeTraceHandler(services.GetRequiredService<IOptions<EvaluationOptions>>().Value.JudgeTrace));

using var host = builder.Build();
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

try
{
    switch (command)
    {
        case "seed":
            await host.Services.GetRequiredService<SeedCommand>().RunAsync(Console.Out, cancellation.Token);
            return 0;
        case "run":
            return await host.Services.GetRequiredService<RunCommand>().RunAsync(Console.Out, cancellation.Token, variants);
        case "compare" when positional.Length == 2:
            {
                var a = ReportWriter.Read(Resolve(positional[0]));
                var b = ReportWriter.Read(Resolve(positional[1]));
                var markdown = CompareCommand.Compare(a, b);
                var file = Path.Combine(RepositoryPaths.Resolve(".data/evaluation"), $"compare-{a.RunId}-vs-{b.RunId}.md");
                await File.WriteAllTextAsync(file, markdown, cancellation.Token);
                Console.WriteLine(markdown);
                Console.WriteLine($"Written to {file}");
                return 0;
            }

        case "sweep" when positional.Length == 1:
            {
                var path = Resolve(positional[0]);
                var report = ReportWriter.Read(path);
                var (markdown, best) = SweepCommand.Sweep(report);
                var file = Path.Combine(Directory.Exists(path) ? path : Path.GetDirectoryName(path)!, "sweep.md");
                await File.WriteAllTextAsync(file, markdown, cancellation.Token);
                Console.WriteLine(
                    $"Best: MinScore {best.Thresholds.MinScore}, ConfidentScore {best.Thresholds.ConfidentScore}, DuplicateOverlap {best.Thresholds.DuplicateOverlap} "
                    + $"(behaviour {best.Behaviour:0.00}, R@topK {best.RecallAtTopK:0.00}). Full grid in {file}");
                return 0;
            }

        case "judge" when positional.Length == 1:
            return await host.Services.GetRequiredService<JudgeCommand>().RunAsync(Resolve(positional[0]), Console.Out, cancellation.Token);

        case "render":
            return Render(host.Services.GetRequiredService<IOptions<EvaluationOptions>>().Value);
        default:
            await Console.Error.WriteLineAsync($"Unknown command '{command}'.{Environment.NewLine}{Usage}");
            return 2;
    }
}
catch (HarnessException ex)
{
    await Console.Error.WriteLineAsync(ex.Message);
    return 1;
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    await Console.Error.WriteLineAsync("Cancelled.");
    return 130;
}

// Report paths may be relative to the working directory or to the repository root.
static string Resolve(string path) => File.Exists(path) || Directory.Exists(path) ? Path.GetFullPath(path) : RepositoryPaths.Resolve(path);

static int Render(EvaluationOptions settings)
{
    var corpusPath = RepositoryPaths.Resolve(settings.CorpusPath);
    var outputPath = RepositoryPaths.Resolve(".data/evaluation/pdf");
    Directory.CreateDirectory(outputPath);
    foreach (var document in CorpusManifest.Load(corpusPath).Documents.Where(d => d.IsPdf))
    {
        for (var i = 0; i < document.Versions.Count; i++)
        {
            var bytes = PdfRenderer.Render(File.ReadAllText(Path.Combine(corpusPath, document.Versions[i])));
            var file = Path.Combine(outputPath, $"{document.Key}-v{i + 1}.pdf");
            File.WriteAllBytes(file, bytes);
            Console.WriteLine($"{file}  sha256 {Convert.ToHexStringLower(SHA256.HashData(bytes))}");
        }
    }

    return 0;
}
