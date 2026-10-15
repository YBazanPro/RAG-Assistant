using Npgsql;
using RagAssistant.Integration.Tests.Fixtures;
using RagAssistant.SharedKernel.Common;
using RagAssistant.SharedKernel.Contracts.Documents;
using static RagAssistant.Integration.Tests.Documents.RetrievalBed;

namespace RagAssistant.Integration.Tests.Documents;

// Spec 011, contract: AC-1 to AC-4, AC-6 to AC-9, AC-11, AC-12, AC-14 to AC-20.
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerTestSession)]
[ParallelLimiter<IngestionParallelLimit>]
public sealed class RetrievalContractTests(PostgresFixture postgres)
{
    [Test]
    public async Task Search_rankedPassages_returnsTopKBestWithAllFields()
    {
        await using var bed = await StartAsync(postgres);
        var (pdf, pdfPassages) = await bed.AddAsync(
            new SeedDocument("Budget Exception Policy", "finance", Language: "en"),
            P(0.9, "Exceptions above ten thousand dollars need director approval.", page: 4, section: ["Approval", "Thresholds"]),
            P(0.55));
        await bed.AddAsync("Travel", P(0.8), P(0.6));
        await bed.AddAsync("Unrelated", P(0.52));

        var result = await bed.SearchAsync(topK: 2);

        await Assert.That(result.IsSuccess).IsTrue();
        var chunks = result.Value.Chunks;
        await Assert.That(chunks.Select(c => Math.Round(c.Score, 4)).SequenceEqual([0.9, 0.8])).IsTrue();
        var best = chunks[0];
        await Assert.That(best.PassageId).IsEqualTo(pdfPassages[0]);
        await Assert.That(best.DocumentId).IsEqualTo(pdf.DocumentId);
        await Assert.That(best.DocumentTitle).IsEqualTo("Budget Exception Policy");
        await Assert.That(best.Language).IsEqualTo("en");
        await Assert.That(best.VersionId).IsEqualTo(pdf.VersionId);
        await Assert.That(best.VersionNumber).IsEqualTo(1);
        await Assert.That(best.SectionPath.SequenceEqual(["Approval", "Thresholds"])).IsTrue();
        await Assert.That(best.Position).IsEqualTo(0);
        await Assert.That(best.PageStart).IsEqualTo(4);
        await Assert.That(best.PageEnd).IsEqualTo(4);
        await Assert.That(best.Text).IsEqualTo("Exceptions above ten thousand dollars need director approval.");
    }

    [Test]
    public async Task Search_supersededVersion_isNeverReturned()
    {
        await using var bed = await StartAsync(postgres);
        var (doc, _) = await bed.AddAsync("Policy", P(0.95));
        var v2 = await bed.Seeder.AddVersionAsync(doc.DocumentId, 2, "Indexed", makeActive: true, DateTimeOffset.UtcNow, embeddingModel: Model);
        await bed.App.ExecuteAsync("UPDATE documents.document_versions SET status = 'Superseded' WHERE id = $1", doc.VersionId);
        var v2Passages = await bed.Seeder.AddEmbeddedPassagesAsync(v2, P(0.6));

        var result = await bed.SearchAsync();

        await Assert.That(result.Value.Chunks.Select(c => c.PassageId).SequenceEqual(v2Passages)).IsTrue();
        await Assert.That(result.Value.Chunks[0].VersionNumber).IsEqualTo(2);
    }

    [Test]
    public async Task Search_archivedDocument_isNeverReturned()
    {
        await using var bed = await StartAsync(postgres);
        await bed.AddAsync(new SeedDocument("Archived", "owner", Archived: true), P(0.95));
        var (_, kept) = await bed.AddAsync("Active", P(0.6));

        var result = await bed.SearchAsync();

        await Assert.That(result.Value.Chunks.Select(c => c.PassageId).SequenceEqual(kept)).IsTrue();
    }

    [Test]
    [Arguments("Queued")]
    [Arguments("Processing")]
    [Arguments("Failed")]
    public async Task Search_versionNotIndexed_isNeverReturned(string status)
    {
        await using var bed = await StartAsync(postgres);
        await bed.AddAsync(new SeedDocument("Pending", "owner", VersionStatus: status), P(0.95));

        var result = await bed.SearchAsync();

        await Assert.That(result.Value.Outcome).IsEqualTo(EvidenceOutcome.NoEvidence);
        await Assert.That(result.Value.Chunks).IsEmpty();
    }

    [Test]
    public async Task Search_otherEmbeddingModel_isExcluded()
    {
        await using var bed = await StartAsync(postgres);
        await bed.AddAsync(new SeedDocument("Old model", "owner", EmbeddingModel: "nomic-embed-text"), P(0.95));
        var (_, kept) = await bed.AddAsync("Current model", P(0.6));

        var result = await bed.SearchAsync();

        await Assert.That(result.Value.Chunks.Select(c => c.PassageId).SequenceEqual(kept)).IsTrue();
    }

    [Test]
    public async Task Search_equalScores_comeOutInTheSameOrderEveryTime()
    {
        await using var bed = await StartAsync(postgres);
        for (var i = 0; i < 4; i++)
        {
            await bed.AddAsync($"Doc {i}", P(0.8, otherAxis: 2 + i), P(0.8, otherAxis: 10 + i));
        }

        var first = await bed.SearchAsync();
        var second = await bed.SearchAsync();

        var order = first.Value.Chunks.Select(c => (c.DocumentId, c.Position)).ToArray();
        await Assert.That(order.Length).IsEqualTo(8);
        await Assert.That(order.SequenceEqual(order.OrderBy(o => o.DocumentId).ThenBy(o => o.Position))).IsTrue();
        await Assert.That(second.Value.Chunks.Select(c => c.PassageId).SequenceEqual(first.Value.Chunks.Select(c => c.PassageId))).IsTrue();
    }

    [Test]
    public async Task Search_languageFilter_isCaseInsensitive()
    {
        await using var bed = await StartAsync(postgres);
        await bed.AddAsync(new SeedDocument("English", "owner", Language: "en"), P(0.9));
        var (_, french) = await bed.AddAsync(new SeedDocument("Français", "owner", Language: "fr"), P(0.6));

        var result = await bed.SearchAsync(filters: new SearchFilters(Language: "FR"));

        await Assert.That(result.Value.Chunks.Select(c => c.PassageId).SequenceEqual(french)).IsTrue();
    }

    [Test]
    public async Task Search_tagsFilter_requiresAllTagsNormalized()
    {
        await using var bed = await StartAsync(postgres);
        var (_, both) = await bed.AddAsync(new SeedDocument("Both", "owner", Tags: ["budget", "finance"]), P(0.6));
        await bed.AddAsync(new SeedDocument("One", "owner", Tags: ["budget"]), P(0.9));

        var result = await bed.SearchAsync(filters: new SearchFilters(Tags: ["Budget ", "finance"]));

        await Assert.That(result.Value.Chunks.Select(c => c.PassageId).SequenceEqual(both)).IsTrue();
    }

    [Test]
    public async Task Search_rareFilterMatch_iterativeScanFindsItBehindManyBetterPassages()
    {
        await using var bed = await StartAsync(postgres);

        // Force the HNSW ordering path on this test's own database. On tables this small the planner would otherwise fetch the
        // few matching passages through b-tree indexes and sort them exactly, and the test would prove nothing about the scan.
        await bed.App.ExecuteAsync($"ALTER DATABASE \"{bed.App.DatabaseName}\" SET enable_seqscan = off");
        await bed.App.ExecuteAsync($"ALTER DATABASE \"{bed.App.DatabaseName}\" SET enable_sort = off");
        bed.App.Db.Clear();
        NpgsqlConnection.ClearAllPools();

        // Realistic neighbourhoods (random directions in a shared subspace, fixed seed): with one private axis per passage the HNSW
        // graph has no structure and leaves late inserts unreachable. Without the iterative scan only ef_search (40) rows come back.
        var random = new Random(42);
        await bed.AddAsync(
            new SeedDocument("Crowd", "owner", Language: "en"),
            [.. Enumerable.Range(0, 300).Select(i => new SeedPassage($"english {i}", FakeEmbeddingGenerator.WithScore(0.9, random)))]);
        var (_, french) = await bed.AddAsync(
            new SeedDocument("Rare", "owner", Language: "fr"),
            [.. Enumerable.Range(0, 5).Select(i => new SeedPassage($"french {i}", FakeEmbeddingGenerator.WithScore(0.6, random)))]);

        var result = await bed.SearchAsync(filters: new SearchFilters(Language: "fr"), topK: 5);

        await Assert.That(result.Value.Chunks.Select(c => c.PassageId).Order().SequenceEqual(french.Order())).IsTrue();
    }

    [Test]
    public async Task Search_emptyCorpus_isNoEvidenceWithoutError()
    {
        await using var bed = await StartAsync(postgres);

        var result = await bed.SearchAsync();

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Outcome).IsEqualTo(EvidenceOutcome.NoEvidence);
        await Assert.That(result.Value.Chunks).IsEmpty();
    }

    [Test]
    public async Task Search_onlyBelowMinScore_isNoEvidence()
    {
        await using var bed = await StartAsync(postgres);
        await bed.AddAsync("Weak", P(0.49), P(0.2));

        var result = await bed.SearchAsync();

        await Assert.That(result.Value.Outcome).IsEqualTo(EvidenceOutcome.NoEvidence);
        await Assert.That(result.Value.Chunks).IsEmpty();
    }

    [Test]
    [Arguments(0.65, EvidenceOutcome.LowConfidence)]
    [Arguments(0.75, EvidenceOutcome.Sufficient)]
    public async Task Search_bestScore_decidesOutcome(double best, EvidenceOutcome expected)
    {
        await using var bed = await StartAsync(postgres);
        await bed.AddAsync("Doc", P(best), P(0.3));

        var result = await bed.SearchAsync();

        await Assert.That(result.Value.Outcome).IsEqualTo(expected);
        await Assert.That(result.Value.Chunks.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Search_duplicateInTop_isReplacedFromThePool()
    {
        await using var bed = await StartAsync(postgres);
        const string same = "Budget exceptions above ten thousand dollars need approval from the finance director.";
        await bed.AddAsync("Original", P(0.95, same, otherAxis: 1), P(0.7));
        await bed.AddAsync("Copy", P(0.94, same, otherAxis: 2), P(0.8));

        var result = await bed.SearchAsync(topK: 3);

        await Assert.That(result.Value.Chunks.Select(c => Math.Round(c.Score, 4)).SequenceEqual([0.95, 0.8, 0.7])).IsTrue();
    }

    [Test]
    public async Task Search_invalidInput_returnsFieldErrors()
    {
        await using var bed = await StartAsync(postgres);
        var tooManyTags = Enumerable.Range(0, 21).Select(i => $"t{i}").ToArray();

        var cases = new (Func<Task<Result<SearchResult>>> Search, string Field)[]
        {
            (() => bed.SearchAsync(topK: 0), "topK"),
            (() => bed.SearchAsync(topK: 21), "topK"),
            (() => bed.SearchAsync("   "), "query"),
            (() => bed.SearchAsync(new string('a', 1001)), "query"),
            (() => bed.SearchAsync(filters: new SearchFilters(Language: "eng")), "language"),
            (() => bed.SearchAsync(filters: new SearchFilters(Tags: tooManyTags)), "tags"),
        };

        foreach (var (search, field) in cases)
        {
            var result = await search();
            await Assert.That(result.IsFailure).IsTrue();
            await Assert.That(result.Error.Code).IsEqualTo(Result.ValidationFailedCode);
            await Assert.That(result.FieldErrors!.ContainsKey(field)).IsTrue();
        }

        await Assert.That(bed.Generator.BatchCalls).IsEqualTo(0);
    }

    [Test]
    public async Task Search_result_reportsThresholdsAndModel()
    {
        await using var bed = await StartAsync(postgres);

        var result = await bed.SearchAsync();

        await Assert.That(result.Value.Thresholds).IsEqualTo(new EvidenceThresholds(0.5, 0.7, 0.8));
        await Assert.That(result.Value.EmbeddingModel).IsEqualTo(Model);
    }

    [Test]
    public async Task Search_sameQueryUpToWhitespace_embedsOnce_otherCaseEmbedsAgain()
    {
        await using var bed = await StartAsync(postgres);

        await bed.SearchAsync("budget  exception");
        await bed.SearchAsync("  budget exception\t");
        var afterWhitespace = bed.Generator.BatchCalls;
        await bed.SearchAsync("Budget exception");

        await Assert.That(afterWhitespace).IsEqualTo(1);
        await Assert.That(bed.Generator.BatchCalls).IsEqualTo(2);
    }

    [Test]
    public async Task Search_documentArchivedAfterASearch_isGoneFromTheNextOne()
    {
        await using var bed = await StartAsync(postgres);
        var (doc, _) = await bed.AddAsync("Soon archived", P(0.9));

        var before = await bed.SearchAsync();
        await bed.App.ExecuteAsync("UPDATE documents.documents SET status = 'Archived' WHERE id = $1", doc.DocumentId);
        var after = await bed.SearchAsync();

        await Assert.That(before.Value.Chunks.Count).IsEqualTo(1);
        await Assert.That(after.Value.Chunks).IsEmpty();
    }

    [Test]
    public async Task Search_modelUnavailable_failsAsUnavailableAndRecovers()
    {
        await using var bed = await StartAsync(postgres);
        await bed.AddAsync("Doc", P(0.9));

        bed.Generator.AlwaysFail = true;
        var failed = await bed.SearchAsync();
        bed.Generator.AlwaysFail = false;
        var recovered = await bed.SearchAsync();

        await Assert.That(failed.IsFailure).IsTrue();
        await Assert.That(failed.Error.Code).IsEqualTo("retrieval.embedding_unavailable");
        await Assert.That(failed.Error.Type).IsEqualTo(ErrorType.Unavailable);
        await Assert.That(recovered.Value.Chunks.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Search_callerCancels_throwsOperationCanceled()
    {
        await using var bed = await StartAsync(postgres);
        bed.Generator.HoldBatches();
        using var cancellation = new CancellationTokenSource();

        var pending = bed.SearchAsync(cancellationToken: cancellation.Token);
        await bed.Generator.WaitUntilHeldAsync();
        await cancellation.CancelAsync();

        await Assert.That(async () => await pending).Throws<OperationCanceledException>();
        bed.Generator.Release();
    }
}
