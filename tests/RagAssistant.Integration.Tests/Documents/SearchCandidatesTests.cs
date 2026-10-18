using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RagAssistant.Integration.Tests.Fixtures;
using static RagAssistant.Integration.Tests.Documents.RetrievalBed;

namespace RagAssistant.Integration.Tests.Documents;

// Spec 018, AC-8 and AC-9: the diagnostic search can return the whole scored pool for the harness's threshold sweep.
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerTestSession)]
[ParallelLimiter<IngestionParallelLimit>]
public sealed class SearchCandidatesTests(PostgresFixture postgres)
{
    private const string SearchUrl = "/api/documents/search?q=budget%20exception";

    private static async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(RetrievalBed bed, string url)
    {
        using var client = bed.App.CreateClientAs(Persona.Maintainer);
        var response = await client.GetAsync(new Uri(url, UriKind.Relative));
        return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    private static string[] PassageIds(JsonElement body, string property) =>
        [.. body.GetProperty(property).EnumerateArray().Select(p => p.GetProperty("passageId").GetString()!)];

    // AC-8
    [Test]
    public async Task Search_withCandidates_listsThePoolWithWhatDroppedEachPassage()
    {
        await using var bed = await StartAsync(postgres);
        const string same = "Budget exceptions above ten thousand dollars need approval from the finance director.";
        var (_, ids) = await bed.AddAsync(
            "Budget Exception Policy",
            P(0.95, same, otherAxis: 1),
            P(0.9, same, otherAxis: 2),
            P(0.8, otherAxis: 3),
            P(0.75, otherAxis: 4),
            P(0.4, otherAxis: 5));

        var (status, body) = await GetAsync(bed, SearchUrl + "&topK=2&candidates=true");
        var (_, plain) = await GetAsync(bed, SearchUrl + "&topK=2");

        await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
        var candidates = body.GetProperty("candidates").EnumerateArray().ToList();
        var dropped = candidates.ToDictionary(c => c.GetProperty("passageId").GetGuid(), c => c.GetProperty("dropped").GetString());
        await Assert.That(candidates.Select(c => c.GetProperty("passageId").GetGuid()).ToArray()).IsEquivalentTo(ids, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(dropped[ids[0]]).IsNull();
        await Assert.That(dropped[ids[1]]).IsEqualTo("duplicate");
        await Assert.That(dropped[ids[2]]).IsNull();
        await Assert.That(dropped[ids[3]]).IsEqualTo("topK");
        await Assert.That(dropped[ids[4]]).IsEqualTo("minScore");
        await Assert.That(candidates[0].GetProperty("language").GetString()).IsEqualTo("en");
        await Assert.That(candidates[0].GetProperty("score").GetDouble()).IsEqualTo(0.95);
        // The ranked result is the same with or without the flag.
        await Assert.That(PassageIds(body, "passages")).IsEquivalentTo(PassageIds(plain, "passages"), TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(plain.TryGetProperty("candidates", out var none) && none.ValueKind != JsonValueKind.Null).IsFalse();
    }

    // AC-9
    [Test]
    public async Task Search_withCandidates_neverListsAnotherUsersPrivatePassages()
    {
        await using var bed = await StartAsync(postgres);
        var (_, secret) = await bed.AddAsync(new SeedDocument("Secret", "owner", Visibility: "Private", CreatedBy: "someone-else"), P(0.95));
        var (_, shared) = await bed.AddAsync("Shared", P(0.9));

        var (_, body) = await GetAsync(bed, SearchUrl + "&candidates=true");

        var listed = body.GetProperty("candidates").EnumerateArray().Select(c => c.GetProperty("passageId").GetGuid()).ToList();
        await Assert.That(listed).Contains(shared[0]);
        await Assert.That(listed).DoesNotContain(secret[0]);
    }

    [Test]
    public async Task Search_withCandidatesNotABoolean_returns400ForCandidates()
    {
        await using var bed = await StartAsync(postgres);

        var (status, body) = await GetAsync(bed, SearchUrl + "&candidates=yes");

        await Assert.That(status).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(body.GetProperty("errors").EnumerateObject().Select(p => p.Name.ToLowerInvariant()).ToArray()).Contains("candidates");
    }
}
