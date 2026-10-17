using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RagAssistant.Modules.Documents.Domain;
using RagAssistant.Modules.Documents.Ingestion;

namespace RagAssistant.Modules.Documents.Tests;

internal sealed class SourceExtractorRegistrationTests
{
    [Test]
    public async Task AddDocumentsModule_registersExactlyOneExtractorPerSourceFormat()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ragassistant-extractors-{Guid.NewGuid():N}");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ragassistant"] = "Host=localhost",
                ["Documents:Storage:Path"] = root,
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMetrics();
        services.AddSingleton(TimeProvider.System);
        services.AddDocumentsModule(configuration);

        try
        {
            await using var provider = services.BuildServiceProvider();
            var formats = provider.GetServices<ISourceExtractor>().Select(e => e.Format).Order().ToArray();

            await Assert.That(formats).IsEquivalentTo(Enum.GetValues<SourceFormat>().Order().ToArray());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
