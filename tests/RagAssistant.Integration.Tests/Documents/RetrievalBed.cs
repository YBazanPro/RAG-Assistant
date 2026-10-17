using Microsoft.Extensions.DependencyInjection;
using RagAssistant.Integration.Tests.Fixtures;
using RagAssistant.SharedKernel.Common;
using RagAssistant.SharedKernel.Contracts.Documents;

namespace RagAssistant.Integration.Tests.Documents;

/// <summary>
/// An application on its own database (search covers the whole corpus, so no other test's documents may be there) with
/// controlled vectors: any query containing "budget" embeds as axis 0, and a passage seeded with <see cref="P"/> scores exactly.
/// </summary>
public sealed class RetrievalBed : IAsyncDisposable
{
    public const string Model = "bge-m3";
    public const string Query = "budget exception threshold";

    /// <summary>Somebody who owns none of the seeded documents (they are imported by <c>seed</c>).</summary>
    public const string Viewer = "test-viewer";

    private static readonly Dictionary<string, string> DefaultSettings = new()
    {
        ["Documents:Retrieval:MinScore"] = "0.5",
        ["Documents:Retrieval:ConfidentScore"] = "0.7",
        ["Documents:Retrieval:DuplicateOverlap"] = "0.8",
    };

    private RetrievalBed(IngestionHarness app)
    {
        App = app;
        Seeder = new CorpusSeeder(app.Db);
    }

    public IngestionHarness App { get; }

    public CorpusSeeder Seeder { get; }

    public FakeEmbeddingGenerator Generator => App.Generator;

    public static async Task<RetrievalBed> StartAsync(PostgresFixture postgres, IReadOnlyDictionary<string, string>? settings = null)
    {
        var merged = new Dictionary<string, string>(DefaultSettings);
        foreach (var (key, value) in settings ?? new Dictionary<string, string>())
        {
            merged[key] = value;
        }

        var generator = new FakeEmbeddingGenerator();
        generator.Script("budget", FakeEmbeddingGenerator.Axis(0));
        return new RetrievalBed(await IngestionHarness.StartAsync(postgres, generator: generator, settings: merged));
    }

    /// <summary>A passage scoring exactly <paramref name="score"/> against <see cref="Query"/>, with text unique enough never to be a duplicate.</summary>
    public static SeedPassage P(double score, string? text = null, int otherAxis = 1, int? page = null, string[]? section = null) =>
        new(text ?? $"passage {Guid.NewGuid()} {score}", FakeEmbeddingGenerator.WithScore(score, otherAxis), page, page, section);

    public async Task<(SeededDocument Document, Guid[] Passages)> AddAsync(SeedDocument document, params SeedPassage[] passages)
    {
        var seeded = await Seeder.AddDocumentAsync(document with { EmbeddingModel = document.EmbeddingModel ?? Model });
        var ids = await Seeder.AddEmbeddedPassagesAsync(seeded.VersionId, passages);
        return (seeded, ids);
    }

    public Task<(SeededDocument Document, Guid[] Passages)> AddAsync(string title, params SeedPassage[] passages) =>
        AddAsync(new SeedDocument(title, "owner"), passages);

    public async Task<Result<SearchResult>> SearchAsync(
        string query = Query, SearchFilters? filters = null, int topK = 8, string viewer = Viewer, CancellationToken cancellationToken = default)
    {
        await using var scope = App.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDocumentSearch>()
            .SearchAsync(query, filters ?? SearchFilters.None, topK, viewer, cancellationToken);
    }

    public async Task<DocumentAvailability> CheckAsync(IReadOnlyCollection<Guid> ids, string viewer = Viewer, CancellationToken cancellationToken = default)
    {
        await using var scope = App.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDocumentCatalog>().CheckAsync(ids, viewer, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, string>> SharedTitlesAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
    {
        await using var scope = App.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDocumentTitles>().GetSharedTitlesAsync(ids, cancellationToken);
    }

    public async ValueTask DisposeAsync() => await App.DisposeAsync();
}
