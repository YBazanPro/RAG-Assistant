using RagAssistant.Integration.Tests.Fixtures;
using RagAssistant.SharedKernel.Contracts.Documents;
using static RagAssistant.Integration.Tests.Documents.RetrievalBed;

namespace RagAssistant.Integration.Tests.Documents;

// Spec 016, AC-11 to AC-15: the Documents contracts apply the visibility rule themselves.
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerTestSession)]
[ParallelLimiter<IngestionParallelLimit>]
public sealed class RetrievalVisibilityTests(PostgresFixture postgres)
{
    private const string Owner = "doc-owner";
    private const string Stranger = "somebody-else";

    private static SeedDocument Private(string title, string[]? tags = null, string language = "en") =>
        new(title, "owner", language, tags, Visibility: "Private", CreatedBy: Owner);

    [Test]
    public async Task Search_returnsPrivatePassagesToTheirOwnerOnly()
    {
        await using var bed = await StartAsync(postgres);
        var (secret, secretPassages) = await bed.AddAsync(Private("Secret"), P(0.95));
        var (shared, _) = await bed.AddAsync("Shared", P(0.8));

        var asOwner = await bed.SearchAsync(viewer: Owner);
        var asStranger = await bed.SearchAsync(viewer: Stranger);

        await Assert.That(asOwner.Value.Chunks.Select(c => c.DocumentId).SequenceEqual([secret.DocumentId, shared.DocumentId])).IsTrue();
        await Assert.That(asStranger.Value.Chunks.Select(c => c.DocumentId).SequenceEqual([shared.DocumentId])).IsTrue();
        await Assert.That(asStranger.Value.Chunks.Any(c => secretPassages.Contains(c.PassageId))).IsFalse();
    }

    [Test]
    public async Task Search_withFiltersNamingAPrivateDocument_neverReturnsItToOthers()
    {
        await using var bed = await StartAsync(postgres);
        var (secret, _) = await bed.AddAsync(Private("Secret", tags: ["hr"]), P(0.95));
        await bed.AddAsync("Shared", P(0.8));

        var byId = await bed.SearchAsync(filters: new SearchFilters(DocumentIds: [secret.DocumentId]), viewer: Stranger);
        var byTag = await bed.SearchAsync(filters: new SearchFilters(Tags: ["hr"]), viewer: Stranger);
        var byLanguage = await bed.SearchAsync(filters: new SearchFilters(Language: "en"), topK: 20, viewer: Stranger);

        await Assert.That(byId.Value.Chunks.Count).IsEqualTo(0);
        await Assert.That(byTag.Value.Chunks.Count).IsEqualTo(0);
        await Assert.That(byLanguage.Value.Chunks.Any(c => c.DocumentId == secret.DocumentId)).IsFalse();
    }

    // AC-12
    [Test]
    public async Task Search_whenOnlyAPrivateDocumentMatches_otherPeopleGetNoEvidence()
    {
        await using var bed = await StartAsync(postgres);
        await bed.AddAsync(Private("Secret"), P(0.95));

        var result = await bed.SearchAsync(viewer: Stranger);

        await Assert.That(result.Value.Outcome).IsEqualTo(EvidenceOutcome.NoEvidence);
    }

    // AC-13
    [Test]
    public async Task Search_privateNearDuplicates_doNotDisplaceSharedResults()
    {
        await using var bed = await StartAsync(postgres);
        const string Text = "Exceptions above ten thousand dollars need director approval.";
        await bed.AddAsync(Private("Secret copy"), P(0.95, Text));
        var (shared, sharedPassages) = await bed.AddAsync("Shared", P(0.9, Text));

        var result = await bed.SearchAsync(topK: 1, viewer: Stranger);

        await Assert.That(result.Value.Chunks.Select(c => c.PassageId).SequenceEqual(sharedPassages)).IsTrue();
        await Assert.That(result.Value.Chunks[0].DocumentId).IsEqualTo(shared.DocumentId);
    }

    // AC-14
    [Test]
    public async Task Catalog_treatsAPrivateDocumentOfSomeoneElseLikeAnUnknownId()
    {
        await using var bed = await StartAsync(postgres);
        var (secret, _) = await bed.AddAsync(Private("Secret"), P(0.9));
        var (shared, _) = await bed.AddAsync("Shared", P(0.9));

        var asStranger = await bed.CheckAsync([secret.DocumentId, shared.DocumentId, Guid.NewGuid()], Stranger);
        var asOwner = await bed.CheckAsync([secret.DocumentId, shared.DocumentId], Owner);

        await Assert.That(asStranger.Known.SequenceEqual([shared.DocumentId])).IsTrue();
        await Assert.That(asStranger.Searchable.SequenceEqual([shared.DocumentId])).IsTrue();
        await Assert.That(asStranger.Titles.ContainsKey(secret.DocumentId)).IsFalse();
        await Assert.That(asOwner.Known.Count).IsEqualTo(2);
        await Assert.That(asOwner.Titles[secret.DocumentId]).IsEqualTo("Secret");
    }

    // Spec 017, AC-21
    [Test]
    public async Task SharedTitles_returnsSharedDocumentsOnlyAndNothingThatRevealsPrivateOrUnknownIds()
    {
        await using var bed = await StartAsync(postgres);
        var (secret, _) = await bed.AddAsync(Private("Secret"), P(0.9));
        var (shared, _) = await bed.AddAsync("Shared", P(0.9));

        var titles = await bed.SharedTitlesAsync([secret.DocumentId, shared.DocumentId, Guid.NewGuid()]);

        await Assert.That(titles.Count).IsEqualTo(1);
        await Assert.That(titles[shared.DocumentId]).IsEqualTo("Shared");
        await Assert.That(titles.ContainsKey(secret.DocumentId)).IsFalse();
    }

    // AC-15
    [Test]
    public async Task Contracts_rejectAnEmptyViewer()
    {
        await using var bed = await StartAsync(postgres);

        var search = await bed.SearchAsync(viewer: " ");

        await Assert.That(search.IsFailure).IsTrue();
        await Assert.That(search.Error.Code).IsEqualTo("validation.failed");
        await Assert.ThrowsAsync<ArgumentException>(async () => await bed.CheckAsync([Guid.NewGuid()], " "));
    }
}
