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
      render    Write the generated sample PDFs to .data/evaluation/pdf, to look at them.
    """;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine(Usage);
    return 0;
}

var command = args[0];
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args[1..],
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Configuration.AddUserSecrets<EvaluationOptions>(optional: true);
builder.Configuration.AddEnvironmentVariables("RAGEVAL_");
builder.Configuration.AddCommandLine(args[1..]);

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
            return await host.Services.GetRequiredService<RunCommand>().RunAsync(Console.Out, cancellation.Token);
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
