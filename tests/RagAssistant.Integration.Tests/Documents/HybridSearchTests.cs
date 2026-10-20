using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RagAssistant.Integration.Tests.Fixtures;
using RagAssistant.SharedKernel.Contracts.Documents;
using static RagAssistant.Integration.Tests.Documents.RetrievalBed;

namespace RagAssistant.Integration.Tests.Documents;

// Spec 019, hybrid search: AC-1 to AC-8, and the endpoint's overrides and stages (AC-15, AC-16).
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerTestSession)]
[ParallelLimiter<IngestionParallelLimit>]
public sealed class HybridSearchTests(PostgresFixture postgres)
{
    private const string CodeQuery = "budget BX-204 approval";
    private const string CodeText = "Budget code BX-204 approval needs the finance director.";

    private static readonly Dictionary<string, string> Hybrid = new() { ["Documents:Retrieval:Hybrid:Enabled"] = "true" };

    /// <summary>Eight passages closer to the query than the BX-204 one, which only keyword search ranks first.</summary>
    private static async Task<Guid> SeedCodeCorpusAsync(RetrievalBed bed)
    {
        var (_, passages) = await bed.AddAsync(
            "Budget Exception Policy",
            [.. Enumerable.Range(0, 8).Select(i => P(0.9 - (i * 0.03), otherAxis: i + 1)), P(0.55, CodeText, otherAxis: 20)]);
        return passages[^1];
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> SearchEndpointAsync(RetrievalBed bed, string query)
    {
        using var client = bed.App.CreateClientAs(Persona.Maintainer);
        var response = await client.GetAsync(new Uri($"/api/documents/search?{query}", UriKind.Relative));
        return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    // AC-1
    [Test]
    public async Task Migration_givesEachPassageTheConfigurationOfItsDocumentLanguage()
    {
        await using var bed = await StartAsync(postgres);
        var (english, _) = await bed.AddAsync(new SeedDocument("English", "o", Language: "en"), P(0.7, "Annual leave is twenty days."));
        var (french, _) = await bed.AddAsync(new SeedDocument("Français", "o", Language: "fr"), P(0.7, "Les congés sont de vingt jours."));
        var (german, _) = await bed.AddAsync(new SeedDocument("Deutsch", "o", Language: "de"), P(0.7, "Urlaub sind zwanzig Tage."));

        var rows = await bed.App.RowsAsync(
            "SELECT v.document_id, p.search_config::text, p.search_vector IS NOT NULL FROM documents.passages p " +
            "JOIN documents.document_versions v ON v.id = p.version_id");

        string Config(Guid documentId) => (string)rows.Single(r => (Guid)r[0]! == documentId)[1]!;
        await Assert.That(Config(english.DocumentId)).IsEqualTo("english");
        await Assert.That(Config(french.DocumentId)).EndsWith("french_unaccent");
        await Assert.That(Config(german.DocumentId)).IsEqualTo("simple");
        await Assert.That(rows.All(r => (bool)r[2]!)).IsTrue();
    }

    // AC-2 and AC-3
    [Test]
    public async Task Search_exactCodeFarFromTheQueryVector_ranksHigherInHybridThanInVectorMode()
    {
        await using var bed = await StartAsync(postgres, Hybrid);
        var code = await SeedCodeCorpusAsync(bed);

        var (_, vector) = await SearchEndpointAsync(bed, $"q={Uri.EscapeDataString(CodeQuery)}&topK=20&mode=vector");
        var (_, hybrid) = await SearchEndpointAsync(bed, $"q={Uri.EscapeDataString(CodeQuery)}&topK=20&queryLanguage=en");

        int RankOf(JsonElement body) => body.GetProperty("passages").EnumerateArray()
            .First(p => p.GetProperty("passageId").GetGuid() == code).GetProperty("rank").GetInt32();
        await Assert.That(RankOf(vector)).IsEqualTo(9);
        await Assert.That(RankOf(hybrid)).IsEqualTo(1);
        await Assert.That(hybrid.GetProperty("mode").GetString()).IsEqualTo("hybrid");
        var stages = hybrid.GetProperty("passages")[0].GetProperty("stages");
        await Assert.That(stages.GetProperty("admittedBy").GetString()).IsEqualTo("both");
        await Assert.That(stages.GetProperty("vectorRank").GetInt32()).IsEqualTo(9);
        await Assert.That(stages.GetProperty("keywordRank").GetInt32()).IsEqualTo(1);
        await Assert.That(stages.GetProperty("cosine").GetDouble()).IsEqualTo(0.55);

        // Vector mode is spec 011's search: cosine order, cosine scores, no keyword or fused stage.
        await Assert.That(vector.GetProperty("mode").GetString()).IsEqualTo("vector");
        var vectorScores = vector.GetProperty("passages").EnumerateArray().Select(p => p.GetProperty("score").GetDouble()).ToList();
        await Assert.That(vectorScores.SequenceEqual(vectorScores.OrderDescending())).IsTrue();
        var vectorStages = vector.GetProperty("passages")[0].GetProperty("stages");
        await Assert.That(vectorStages.GetProperty("admittedBy").GetString()).IsEqualTo("vector");
        await Assert.That(vectorStages.GetProperty("fusedScore").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    // AC-4
    [Test]
    public async Task Search_frenchQueryWithAccents_matchesAFrenchPassageWithout()
    {
        await using var bed = await StartAsync(postgres, Hybrid);
        bed.Generator.Script("congés", FakeEmbeddingGenerator.Axis(0));
        var (_, passages) = await bed.AddAsync(
            new SeedDocument("Congés", "o", Language: "fr"), P(0.7, "Les conges payes sont de vingt-cinq jours ouvrables par an."));

        var result = await bed.SearchAsync("congés payés", new SearchFilters(QueryLanguage: "fr"));

        var chunk = result.Value.Chunks.Single();
        await Assert.That(chunk.PassageId).IsEqualTo(passages[0]);
        await Assert.That(chunk.Stages!.KeywordRank).IsEqualTo(1);
        await Assert.That(chunk.Stages.AdmittedBy).IsEqualTo(AdmittedBy.Both);
        await Assert.That(result.Value.Mode).IsEqualTo(RetrievalMode.Hybrid);
    }

    // AC-5
    [Test]
    public async Task LanguageChange_movesThePassagesToTheNewConfiguration()
    {
        await using var bed = await StartAsync(postgres);
        var (document, _) = await bed.AddAsync(new SeedDocument("Policy", "o", Language: "en"), P(0.7, "Annual leave."), P(0.6, "Sick leave."));

        await bed.App.ExecuteAsync("UPDATE documents.documents SET language = 'fr' WHERE id = $1", document.DocumentId);

        var configs = await bed.App.RowsAsync(
            "SELECT p.search_config::text FROM documents.passages p JOIN documents.document_versions v ON v.id = p.version_id " +
            "WHERE v.document_id = $1", document.DocumentId);
        await Assert.That(configs.Count).IsEqualTo(2);
        await Assert.That(configs.All(r => ((string)r[0]!).EndsWith("french_unaccent", StringComparison.Ordinal))).IsTrue();
    }

    // AC-6
    [Test]
    public async Task Search_privateDocumentOfSomeoneElse_isInNeitherPool()
    {
        await using var bed = await StartAsync(postgres, Hybrid);
        await bed.AddAsync(new SeedDocument("Hidden", "o", Visibility: "Private", CreatedBy: "someone-else"), P(0.95, CodeText));
        var (_, visible) = await bed.AddAsync("Visible", P(0.6, "Budget limits for BX-204 are set yearly."));

        var result = await bed.SearchAsync(CodeQuery, new SearchFilters(QueryLanguage: "en"), topK: 20);
        var (_, diagnostics) = await SearchEndpointAsync(bed, $"q={Uri.EscapeDataString(CodeQuery)}&topK=20&candidates=true&queryLanguage=en");

        await Assert.That(result.Value.Chunks.Select(c => c.PassageId).SequenceEqual(visible)).IsTrue();
        var pool = diagnostics.GetProperty("candidates").EnumerateArray().Select(c => c.GetProperty("passageId").GetGuid()).ToList();
        await Assert.That(pool.SequenceEqual(visible)).IsTrue();
    }

    // AC-7
    [Test]
    public async Task Search_onlyStopWords_returnsTheVectorResult()
    {
        await using var bed = await StartAsync(postgres, Hybrid);
        bed.Generator.Script("the of and", FakeEmbeddingGenerator.Axis(0));
        await bed.AddAsync("Policy", P(0.9), P(0.8, otherAxis: 2), P(0.7, otherAxis: 3));

        var (hybridStatus, hybrid) = await SearchEndpointAsync(bed, "q=the%20of%20and&queryLanguage=en");
        var (_, vector) = await SearchEndpointAsync(bed, "q=the%20of%20and&mode=vector");

        static List<Guid> Ids(JsonElement body) => [.. body.GetProperty("passages").EnumerateArray().Select(p => p.GetProperty("passageId").GetGuid())];
        await Assert.That(hybridStatus).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Ids(hybrid).Count).IsEqualTo(3);
        await Assert.That(Ids(hybrid).SequenceEqual(Ids(vector))).IsTrue();
        await Assert.That(hybrid.GetProperty("passages").EnumerateArray().All(p => p.GetProperty("stages").GetProperty("admittedBy").GetString() == "vector")).IsTrue();
    }

    // AC-8
    [Test]
    public async Task Search_filters_applyToTheKeywordPoolToo()
    {
        await using var bed = await StartAsync(postgres, Hybrid);
        var (finance, financePassages) = await bed.AddAsync(new SeedDocument("Finance", "o", Tags: ["finance"]), P(0.6, CodeText));
        var (hr, hrPassages) = await bed.AddAsync(new SeedDocument("HR", "o", Tags: ["hr"]), P(0.6, "Budget BX-204 approval for hiring."));

        var byTag = await bed.SearchAsync(CodeQuery, new SearchFilters(Tags: ["finance"], QueryLanguage: "en"));
        var byDocument = await bed.SearchAsync(CodeQuery, new SearchFilters(DocumentIds: [hr.DocumentId], QueryLanguage: "en"));
        var byLanguage = await bed.SearchAsync(CodeQuery, new SearchFilters(Language: "fr", QueryLanguage: "en"));

        await Assert.That(byTag.Value.Chunks.Select(c => c.PassageId).SequenceEqual(financePassages)).IsTrue();
        await Assert.That(byTag.Value.Chunks.Single().Stages!.KeywordRank).IsEqualTo(1);
        await Assert.That(byDocument.Value.Chunks.Select(c => c.PassageId).SequenceEqual(hrPassages)).IsTrue();
        await Assert.That(byLanguage.Value.Outcome).IsEqualTo(EvidenceOutcome.NoEvidence);
        await Assert.That(finance.DocumentId).IsNotEqualTo(hr.DocumentId);
    }

    // AC-23: any of the question's terms is enough to match; how many of them the passage holds is its coverage.
    [Test]
    public async Task Search_passageWithoutEveryTermOfTheQuestion_isStillAKeywordMatch()
    {
        await using var bed = await StartAsync(postgres, Hybrid);
        var (_, passages) = await bed.AddAsync("Budget Exception Policy", P(0.6, CodeText));

        var result = await bed.SearchAsync("budget BX-204 for travel", new SearchFilters(QueryLanguage: "en"));

        var stages = result.Value.Chunks.Single(c => c.PassageId == passages[0]).Stages!;
        await Assert.That(stages.KeywordRank).IsEqualTo(1);
        await Assert.That(stages.AdmittedBy).IsEqualTo(AdmittedBy.Both);
        // budget, bx and -204 (the parser's split of "BX-204") of those and travel.
        await Assert.That(stages.KeywordCoverage).IsEqualTo(0.75);
    }

    // AC-24
    [Test]
    public async Task Search_keywordMatchBelowTheFloor_isAdmittedInHybridModeOnly()
    {
        await using var bed = await StartAsync(postgres, Hybrid);
        var (_, passages) = await bed.AddAsync("Budget Exception Policy", P(0.3, CodeText));

        var hybrid = await bed.SearchAsync(CodeQuery, new SearchFilters(QueryLanguage: "en"));
        var (_, vector) = await SearchEndpointAsync(bed, $"q={Uri.EscapeDataString(CodeQuery)}&mode=vector");

        var chunk = hybrid.Value.Chunks.Single();
        await Assert.That(chunk.PassageId).IsEqualTo(passages[0]);
        await Assert.That(chunk.Score).IsLessThan(0.5);
        await Assert.That(chunk.Stages!.KeywordCoverage).IsEqualTo(1.0);
        await Assert.That(hybrid.Value.Outcome).IsEqualTo(EvidenceOutcome.LowConfidence);
        await Assert.That(vector.GetProperty("outcome").GetString()).IsEqualTo("noEvidence");
    }

    // AC-25
    [Test]
    public async Task Search_keywordMatchOnOneCommonWord_staysBelowTheFloor()
    {
        await using var bed = await StartAsync(postgres, Hybrid);
        await bed.AddAsync("Budget Exception Policy", P(0.3, "The budget is reviewed every spring."));

        var result = await bed.SearchAsync(CodeQuery, new SearchFilters(QueryLanguage: "en"));
        var (_, diagnostics) = await SearchEndpointAsync(bed, $"q={Uri.EscapeDataString(CodeQuery)}&queryLanguage=en&candidates=true");

        await Assert.That(result.Value.Outcome).IsEqualTo(EvidenceOutcome.NoEvidence);
        var candidate = diagnostics.GetProperty("candidates")[0];
        await Assert.That(candidate.GetProperty("dropped").GetString()).IsEqualTo("minScore");
        // budget alone, of budget, bx, -204 and approv.
        await Assert.That(candidate.GetProperty("stages").GetProperty("keywordCoverage").GetDouble()).IsEqualTo(0.25);
    }

    // AC-15
    [Test]
    public async Task Endpoint_overrides_reportModeRerankAndStagesWhateverTheConfiguration()
    {
        await using var bed = await StartAsync(postgres);
        await SeedCodeCorpusAsync(bed);
        bed.App.Reranker.Score = (_, text) => text.Contains("BX-204", StringComparison.Ordinal) ? 0.95 : 0.3;

        var (_, plain) = await SearchEndpointAsync(bed, $"q={Uri.EscapeDataString(CodeQuery)}&mode=vector&rerank=false");
        var (_, full) = await SearchEndpointAsync(bed, $"q={Uri.EscapeDataString(CodeQuery)}&mode=hybrid&rerank=true&queryLanguage=en");

        await Assert.That(plain.GetProperty("mode").GetString()).IsEqualTo("vector");
        await Assert.That(plain.GetProperty("rerank").GetString()).IsEqualTo("off");
        await Assert.That(plain.GetProperty("passages")[0].GetProperty("stages").GetProperty("rerankScore").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(full.GetProperty("mode").GetString()).IsEqualTo("hybrid");
        await Assert.That(full.GetProperty("rerank").GetString()).IsEqualTo("applied");
        await Assert.That(full.GetProperty("rerankModel").GetString()).IsEqualTo("fake-reranker");
        await Assert.That(full.GetProperty("rerankMs").GetInt32()).IsGreaterThanOrEqualTo(0);
        var top = full.GetProperty("passages")[0];
        await Assert.That(top.GetProperty("score").GetDouble()).IsEqualTo(0.95);
        await Assert.That(top.GetProperty("stages").GetProperty("rerankScore").GetDouble()).IsEqualTo(0.95);
        await Assert.That(full.GetProperty("thresholds").GetProperty("minScore").GetDouble()).IsEqualTo(0.1);
    }

    // AC-16
    [Test]
    [Arguments("mode=fuzzy", "mode")]
    [Arguments("rerank=maybe", "rerank")]
    [Arguments("queryLanguage=french", "queryLanguage")]
    public async Task Endpoint_invalidOverride_isAValidationError(string parameter, string field)
    {
        await using var bed = await StartAsync(postgres);

        var (status, body) = await SearchEndpointAsync(bed, $"q=budget&{parameter}");

        await Assert.That(status).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(body.GetProperty("errors").TryGetProperty(field, out _)).IsTrue();
    }
}
