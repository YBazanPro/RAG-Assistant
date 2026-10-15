using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RagAssistant.Modules.Documents.Options;

namespace RagAssistant.Modules.Documents.Tests;

internal sealed class OptionsValidationTests
{
    private static ServiceProvider Build(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:ragassistant"] = "Host=localhost" })
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDocumentsModule(configuration);
        return services.BuildServiceProvider();
    }

    [Test]
    public async Task Options_defaults_areValid()
    {
        await using var provider = Build();

        await Assert.That(provider.GetRequiredService<IOptions<ChunkingOptions>>().Value.MaxTokens).IsEqualTo(400);
        await Assert.That(provider.GetRequiredService<IOptions<IngestionOptions>>().Value.EmbeddingBatchSize).IsEqualTo(16);
    }

    [Test]
    [Arguments("Documents:Chunking:MaxTokens", "0")]
    [Arguments("Documents:Chunking:OverlapTokens", "0")]
    [Arguments("Documents:Chunking:MinTokens", "-1")]
    [Arguments("Documents:Chunking:OverlapTokens", "400")]
    [Arguments("Documents:Chunking:MinTokens", "400")]
    public async Task ChunkingOptions_invalid_failOnResolve(string key, string value)
    {
        await using var provider = Build((key, value));

        await Assert.That(() => provider.GetRequiredService<IOptions<ChunkingOptions>>().Value)
            .Throws<OptionsValidationException>();
    }

    [Test]
    [Arguments("Documents:Ingestion:MaxConcurrentRuns", "0")]
    [Arguments("Documents:Ingestion:EmbeddingBatchSize", "0")]
    [Arguments("Documents:Ingestion:EmbeddingMaxRetries", "-1")]
    [Arguments("Documents:Ingestion:EmbeddingMaxRetries", "0")]
    [Arguments("Documents:Ingestion:EmbeddingTimeout", "00:00:00")]
    [Arguments("Documents:Ingestion:EmbeddingRetryBaseDelay", "00:00:00")]
    [Arguments("Documents:Ingestion:ProbeInterval", "00:00:00")]
    [Arguments("Documents:Ingestion:ProgressPollInterval", "00:00:00")]
    [Arguments("Documents:Ingestion:HeartbeatInterval", "00:00:00")]
    public async Task IngestionOptions_invalid_failOnResolve(string key, string value)
    {
        await using var provider = Build((key, value));

        await Assert.That(() => provider.GetRequiredService<IOptions<IngestionOptions>>().Value)
            .Throws<OptionsValidationException>();
    }

    [Test]
    public async Task RetrievalOptions_defaults_areValid()
    {
        await using var provider = Build();

        var options = provider.GetRequiredService<IOptions<RetrievalOptions>>().Value;
        await Assert.That(options.DefaultTopK).IsEqualTo(8);
        await Assert.That(options.PoolSize(8)).IsEqualTo(32);
        await Assert.That(options.PoolSize(20)).IsEqualTo(80);
    }

    [Test]
    [Arguments("Documents:Retrieval:DefaultTopK", "0")]
    [Arguments("Documents:Retrieval:DefaultTopK", "21")]
    [Arguments("Documents:Retrieval:MaxTopK", "0")]
    [Arguments("Documents:Retrieval:CandidateMultiplier", "0")]
    [Arguments("Documents:Retrieval:MaxCandidates", "19")]
    [Arguments("Documents:Retrieval:MinScore", "-0.1")]
    [Arguments("Documents:Retrieval:MinScore", "0.7")]
    [Arguments("Documents:Retrieval:ConfidentScore", "1.1")]
    [Arguments("Documents:Retrieval:ConfidentScore", "0.4")]
    [Arguments("Documents:Retrieval:DuplicateOverlap", "0")]
    [Arguments("Documents:Retrieval:DuplicateOverlap", "1.5")]
    [Arguments("Documents:Retrieval:QueryEmbeddingTimeout", "00:00:00")]
    [Arguments("Documents:Retrieval:QueryEmbeddingCacheDuration", "00:00:00")]
    public async Task RetrievalOptions_invalid_failOnResolve(string key, string value)
    {
        await using var provider = Build((key, value));

        await Assert.That(() => provider.GetRequiredService<IOptions<RetrievalOptions>>().Value)
            .Throws<OptionsValidationException>();
    }
}
