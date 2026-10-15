using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RagAssistant.Integration.Tests.Fixtures;

namespace RagAssistant.Integration.Tests.Documents;

// Spec 008, AC-1 to AC-7 and the list half of AC-14. Every test seeds under its own owner and filters on it, because
// the factory (and its database) is shared by the whole session.
[ClassDataSource<RagAssistantAppFactory>(Shared = SharedType.PerTestSession)]
public sealed class ListDocumentsTests(RagAssistantAppFactory factory)
{
    private static readonly DateTimeOffset Base = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private CorpusSeeder Seeder => new(factory.Services.GetRequiredService<NpgsqlDataSource>());

    private static string NewOwner() => $"owner-{Guid.NewGuid():N}";

    private async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(string url, Persona persona = Persona.KnowledgeSeeker)
    {
        using var client = factory.CreateClientAs(persona);
        var response = await client.GetAsync(url);
        var body = response.Content.Headers.ContentLength == 0 ? default : await response.Content.ReadFromJsonAsync<JsonElement>();
        return (response.StatusCode, body);
    }

    private static string[] Titles(JsonElement body) => [.. body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()!)];

    [Test]
    public async Task List_documentsInEveryStatus_excludesArchivedAndMapsPassageCount()
    {
        var owner = NewOwner();
        await Seeder.AddDocumentAsync(new($"{owner} active", owner, UpdatedAt: Base.AddDays(1), Passages: 3));
        await Seeder.AddDocumentAsync(new($"{owner} queued", owner, VersionStatus: "Queued", UpdatedAt: Base.AddDays(2)));
        await Seeder.AddDocumentAsync(new($"{owner} processing", owner, VersionStatus: "Processing", UpdatedAt: Base.AddDays(3)));
        await Seeder.AddDocumentAsync(new($"{owner} failed", owner, VersionStatus: "Failed", UpdatedAt: Base.AddDays(4)));
        await Seeder.AddDocumentAsync(new($"{owner} archived", owner, Archived: true, UpdatedAt: Base.AddDays(5), Passages: 1));

        var (status, body) = await GetAsync($"/api/documents?owner={owner}");

        await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body.GetProperty("totalCount").GetInt32()).IsEqualTo(4);
        await Assert.That((Titles(body)).Same($"{owner} failed", $"{owner} processing", $"{owner} queued", $"{owner} active")).IsTrue();
        var items = body.GetProperty("items").EnumerateArray().ToArray();
        await Assert.That((items.Select(i => i.GetProperty("status").GetString())).Same("failed", "processing", "queued", "active")).IsTrue();
        await Assert.That(items[3].GetProperty("passageCount").GetInt32()).IsEqualTo(3);
        await Assert.That(items[0].GetProperty("passageCount").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(items[3].GetProperty("activeVersionNumber").GetInt32()).IsEqualTo(1);

        var (_, archived) = await GetAsync($"/api/documents?owner={owner}&status=archived");
        await Assert.That(archived.GetProperty("totalCount").GetInt32()).IsEqualTo(1);
        await Assert.That((Titles(archived)).Same($"{owner} archived")).IsTrue();
    }

    [Test]
    [Arguments("title")]
    [Arguments("-title")]
    [Arguments("updated")]
    [Arguments("-updated")]
    [Arguments("effectiveDate")]
    [Arguments("-effectiveDate")]
    public async Task List_pagedUnderEverySort_hasNoOverlapOrGap(string sort)
    {
        var owner = NewOwner();
        for (var i = 0; i < 5; i++)
        {
            await Seeder.AddDocumentAsync(new($"Doc {i}", owner, UpdatedAt: Base.AddDays(i), EffectiveDate: i % 2 == 0 ? new DateOnly(2026, 1, 1 + i) : null));
        }

        var seen = new List<string>();
        int[] expectedSizes = [2, 2, 1];
        for (var page = 1; page <= 3; page++)
        {
            var (_, body) = await GetAsync($"/api/documents?owner={owner}&pageSize=2&page={page}&sort={sort}");
            await Assert.That(body.GetProperty("items").GetArrayLength()).IsEqualTo(expectedSizes[page - 1]);
            await Assert.That(body.GetProperty("totalCount").GetInt32()).IsEqualTo(5);
            seen.AddRange(Titles(body));
        }

        await Assert.That(seen.Distinct().Count()).IsEqualTo(5);

        var (_, beyond) = await GetAsync($"/api/documents?owner={owner}&pageSize=2&page=9");
        await Assert.That(beyond.GetProperty("items").GetArrayLength()).IsEqualTo(0);
        await Assert.That(beyond.GetProperty("totalCount").GetInt32()).IsEqualTo(5);
        await Assert.That(beyond.GetProperty("page").GetInt32()).IsEqualTo(9);
    }

    [Test]
    public async Task List_sorts_orderCorrectlyAndPutMissingEffectiveDatesLast()
    {
        var owner = NewOwner();
        await Seeder.AddDocumentAsync(new("B doc", owner, UpdatedAt: Base.AddDays(1), EffectiveDate: new DateOnly(2026, 3, 1)));
        await Seeder.AddDocumentAsync(new("A doc", owner, UpdatedAt: Base.AddDays(3)));
        await Seeder.AddDocumentAsync(new("C doc", owner, UpdatedAt: Base.AddDays(2), EffectiveDate: new DateOnly(2026, 1, 1)));

        async Task<string[]> OrderAsync(string sort) => Titles((await GetAsync($"/api/documents?owner={owner}&sort={sort}")).Body);

        await Assert.That((await OrderAsync("title")).Same("A doc", "B doc", "C doc")).IsTrue();
        await Assert.That((await OrderAsync("-title")).Same("C doc", "B doc", "A doc")).IsTrue();
        await Assert.That((await OrderAsync("updated")).Same("B doc", "C doc", "A doc")).IsTrue();
        await Assert.That((await OrderAsync("-updated")).Same("A doc", "C doc", "B doc")).IsTrue();
        await Assert.That((await OrderAsync("effectiveDate")).Same("C doc", "B doc", "A doc")).IsTrue();
        await Assert.That((await OrderAsync("-effectiveDate")).Same("B doc", "C doc", "A doc")).IsTrue();
    }

    [Test]
    public async Task List_lastActivity_usesIndexedAtWhenItIsNewerThanUpdatedAt()
    {
        var owner = NewOwner();
        await Seeder.AddDocumentAsync(new("Indexed later", owner, UpdatedAt: Base, IndexedAt: Base.AddDays(10)));
        await Seeder.AddDocumentAsync(new("Updated later", owner, UpdatedAt: Base.AddDays(5), IndexedAt: Base.AddDays(1)));

        var (_, body) = await GetAsync($"/api/documents?owner={owner}");

        await Assert.That((Titles(body)).Same("Indexed later", "Updated later")).IsTrue();
        await Assert.That(body.GetProperty("items")[0].GetProperty("lastActivityAt").GetDateTimeOffset()).IsEqualTo(Base.AddDays(10));
    }

    [Test]
    public async Task List_titleSearch_isCaseInsensitiveContainsWithLiteralWildcards()
    {
        var owner = NewOwner();
        await Seeder.AddDocumentAsync(new("Budget Policy", owner));
        await Seeder.AddDocumentAsync(new("Rate is 50% off", owner));
        await Seeder.AddDocumentAsync(new("a_b file", owner));
        await Seeder.AddDocumentAsync(new("axb file", owner));

        await Assert.That((Titles((await GetAsync($"/api/documents?owner={owner}&q=bUDGet")).Body)).Same("Budget Policy")).IsTrue();
        await Assert.That((Titles((await GetAsync($"/api/documents?owner={owner}&q=50%25")).Body)).Same("Rate is 50% off")).IsTrue();
        await Assert.That((Titles((await GetAsync($"/api/documents?owner={owner}&q=a_b")).Body)).Same("a_b file")).IsTrue();
    }

    [Test]
    public async Task List_filters_combineWithAnd()
    {
        var owner = NewOwner();
        await Seeder.AddDocumentAsync(new("Match", owner, Language: "fr", VersionStatus: "Failed", EffectiveDate: new DateOnly(2026, 5, 10)));
        await Seeder.AddDocumentAsync(new("Wrong language", owner, Language: "en", VersionStatus: "Failed", EffectiveDate: new DateOnly(2026, 5, 10)));
        await Seeder.AddDocumentAsync(new("Wrong status", owner, Language: "fr", EffectiveDate: new DateOnly(2026, 5, 10)));
        await Seeder.AddDocumentAsync(new("No date", owner, Language: "fr", VersionStatus: "Failed"));
        await Seeder.AddDocumentAsync(new("Out of range", owner, Language: "fr", VersionStatus: "Failed", EffectiveDate: new DateOnly(2027, 1, 1)));

        var (_, all) = await GetAsync($"/api/documents?owner={owner}&status=failed&language=FR&effectiveFrom=2026-05-10&effectiveTo=2026-12-31");
        await Assert.That((Titles(all)).Same("Match")).IsTrue();

        var (_, failed) = await GetAsync($"/api/documents?owner={owner}&status=failed");
        await Assert.That(failed.GetProperty("totalCount").GetInt32()).IsEqualTo(4);

        var (_, dated) = await GetAsync($"/api/documents?owner={owner}&effectiveFrom=2026-01-01");
        await Assert.That(Titles(dated)).DoesNotContain("No date");
    }

    [Test]
    public async Task List_tags_requireAllGivenTags()
    {
        var owner = NewOwner();
        await Seeder.AddDocumentAsync(new("Both", owner, Tags: ["budget", "finance"]));
        await Seeder.AddDocumentAsync(new("One", owner, Tags: ["budget"]));
        await Seeder.AddDocumentAsync(new("None", owner));

        var (_, body) = await GetAsync($"/api/documents?owner={owner}&tags=Budget%20&tags=finance");

        await Assert.That((Titles(body)).Same("Both")).IsTrue();
    }

    [Test]
    [Arguments("page=0", "page")]
    [Arguments("page=abc", "page")]
    [Arguments("pageSize=0", "pageSize")]
    [Arguments("pageSize=101", "pageSize")]
    [Arguments("sort=bogus", "sort")]
    [Arguments("status=foo", "status")]
    [Arguments("language=eng", "language")]
    [Arguments("effectiveFrom=2026-13-01", "effectiveFrom")]
    [Arguments("effectiveFrom=2026-05-02&effectiveTo=2026-05-01", "effectiveFrom")]
    public async Task List_invalidQuery_returns400WithFieldError(string query, string field)
    {
        var (status, body) = await GetAsync($"/api/documents?{query}");

        await Assert.That(status).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(body.GetProperty("code").GetString()).IsEqualTo("validation.failed");
        await Assert.That(body.GetProperty("errors").TryGetProperty(field, out _)).IsTrue();
    }

    [Test]
    public async Task List_tooManyTagsOrLongQuery_returns400()
    {
        var tags = string.Join('&', Enumerable.Range(0, 21).Select(i => $"tags=t{i}"));
        var (tagStatus, tagBody) = await GetAsync($"/api/documents?{tags}");
        var (qStatus, qBody) = await GetAsync($"/api/documents?q={new string('x', 201)}");

        await Assert.That(tagStatus).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(tagBody.GetProperty("errors").TryGetProperty("tags", out _)).IsTrue();
        await Assert.That(qStatus).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(qBody.GetProperty("errors").TryGetProperty("q", out _)).IsTrue();
    }

    [Test]
    public async Task List_asAnonymous_returns401()
    {
        var (status, _) = await GetAsync("/api/documents", Persona.Anonymous);

        await Assert.That(status).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    [Arguments(Persona.KnowledgeSeeker)]
    [Arguments(Persona.ContentOwner)]
    [Arguments(Persona.Maintainer)]
    public async Task List_asEveryRole_returns200(Persona persona)
    {
        var (status, _) = await GetAsync("/api/documents", persona);

        await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
    }
}
