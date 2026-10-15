using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RagAssistant.Integration.Tests.Fixtures;

namespace RagAssistant.Integration.Tests.Documents;

// Spec 008, AC-8 to AC-14: facets, versions and passages.
[ClassDataSource<RagAssistantAppFactory>(Shared = SharedType.PerTestSession)]
public sealed class BrowseDocumentTests(RagAssistantAppFactory factory)
{
    private static readonly DateTimeOffset Base = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private CorpusSeeder Seeder => new(factory.Services.GetRequiredService<NpgsqlDataSource>());

    private async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(string url, Persona persona = Persona.KnowledgeSeeker)
    {
        using var client = factory.CreateClientAs(persona);
        var response = await client.GetAsync(url);
        var body = response.Content.Headers.ContentLength == 0 ? default : await response.Content.ReadFromJsonAsync<JsonElement>();
        return (response.StatusCode, body);
    }

    [Test]
    public async Task Facets_countOwnersLanguagesAndTags_includingArchived()
    {
        var owner = $"owner-{Guid.NewGuid():N}";
        var tag = $"tag{Guid.NewGuid():N}";
        await Seeder.AddDocumentAsync(new("One", owner, Tags: [tag, "zz-other"]));
        await Seeder.AddDocumentAsync(new("Two", owner, Tags: [tag], Archived: true));
        await Seeder.AddDocumentAsync(new("Three", owner, Language: "fr"));

        var (status, body) = await GetAsync("/api/documents/facets");

        await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
        var ownerFacet = body.GetProperty("owners").EnumerateArray().Single(o => o.GetProperty("value").GetString() == owner);
        await Assert.That(ownerFacet.GetProperty("count").GetInt32()).IsEqualTo(3);
        var tagFacet = body.GetProperty("tags").EnumerateArray().Single(o => o.GetProperty("value").GetString() == tag);
        await Assert.That(tagFacet.GetProperty("count").GetInt32()).IsEqualTo(2);
        await Assert.That(body.GetProperty("languages").EnumerateArray().Any(l => l.GetProperty("value").GetString() == "fr")).IsTrue();

        var counts = body.GetProperty("owners").EnumerateArray().Select(o => o.GetProperty("count").GetInt32()).ToArray();
        await Assert.That(counts.SequenceEqual(counts.OrderByDescending(c => c))).IsTrue();
    }

    [Test]
    public async Task Versions_supersededActiveAndFailedRetry_areOrderedAndMarked()
    {
        var owner = $"owner-{Guid.NewGuid():N}";
        var doc = await Seeder.AddDocumentAsync(new("Versioned", owner, VersionStatus: "Superseded", Passages: 1));
        await Seeder.AddRunAsync(doc.VersionId, 1, "Succeeded", "Completed");
        var v2 = await Seeder.AddVersionAsync(doc.DocumentId, 2, "Indexed", makeActive: true, Base, Base, passages: 2);
        await Seeder.AddRunAsync(v2, 1, "Failed", "Embedding", "ingestion.embedding_failed", "boom");
        await Seeder.AddRunAsync(v2, 2, "Succeeded", "Completed");

        var (status, body) = await GetAsync($"/api/documents/{doc.DocumentId}/versions");

        await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
        var items = body.GetProperty("items").EnumerateArray().ToArray();
        await Assert.That((items.Select(i => i.GetProperty("number").GetInt32())).Same(2, 1)).IsTrue();
        await Assert.That((items.Select(i => i.GetProperty("isActive").GetBoolean())).Same(true, false)).IsTrue();
        await Assert.That(items[1].GetProperty("status").GetString()).IsEqualTo("superseded");
        await Assert.That(items[0].TryGetProperty("content", out _)).IsFalse();

        var runs = items[0].GetProperty("runs").EnumerateArray().ToArray();
        await Assert.That((runs.Select(r => r.GetProperty("attempt").GetInt32())).Same(2, 1)).IsTrue();
        await Assert.That(runs[1].GetProperty("errorCode").GetString()).IsEqualTo("ingestion.embedding_failed");
        await Assert.That(runs[1].GetProperty("stage").GetString()).IsEqualTo("embedding");
    }

    [Test]
    public async Task Passages_of61_returnFirstPageOf50InPositionOrderWithoutEmbeddings()
    {
        var doc = await Seeder.AddDocumentAsync(new("Big", $"owner-{Guid.NewGuid():N}", Passages: 61));

        var (status, body) = await GetAsync($"/api/documents/{doc.DocumentId}/versions/1/passages");

        await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
        var items = body.GetProperty("items").EnumerateArray().ToArray();
        await Assert.That(items.Length).IsEqualTo(50);
        await Assert.That(items.Select(i => i.GetProperty("position").GetInt32()).SequenceEqual(Enumerable.Range(0, 50))).IsTrue();
        await Assert.That(body.GetProperty("totalCount").GetInt32()).IsEqualTo(61);
        await Assert.That(body.GetProperty("pageSize").GetInt32()).IsEqualTo(50);
        await Assert.That(body.GetProperty("versionNumber").GetInt32()).IsEqualTo(1);
        await Assert.That(body.GetProperty("versionStatus").GetString()).IsEqualTo("indexed");
        await Assert.That(body.GetProperty("isActive").GetBoolean()).IsTrue();
        await Assert.That(items[0].TryGetProperty("embedding", out _)).IsFalse();
        await Assert.That(items[0].GetProperty("text").GetString()).IsEqualTo("Passage 0 <script>");
    }

    [Test]
    public async Task Passages_focus_returnsThePageContainingThePassage()
    {
        var doc = await Seeder.AddDocumentAsync(new("Big", $"owner-{Guid.NewGuid():N}", Passages: 61));
        var focus = await Seeder.PassageIdAsync(doc.VersionId, 55);

        var (status, body) = await GetAsync($"/api/documents/{doc.DocumentId}/versions/1/passages?focus={focus}&page=1");

        await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body.GetProperty("page").GetInt32()).IsEqualTo(2);
        await Assert.That(body.GetProperty("items").EnumerateArray().Any(i => i.GetProperty("id").GetGuid() == focus)).IsTrue();
    }

    [Test]
    public async Task Passages_focusFromAnotherVersionOrUnknown_returns404()
    {
        var owner = $"owner-{Guid.NewGuid():N}";
        var doc = await Seeder.AddDocumentAsync(new("One", owner, Passages: 2));
        var other = await Seeder.AddDocumentAsync(new("Two", owner, Passages: 2));
        var foreign = await Seeder.PassageIdAsync(other.VersionId, 0);

        var (foreignStatus, foreignBody) = await GetAsync($"/api/documents/{doc.DocumentId}/versions/1/passages?focus={foreign}");
        var (unknownStatus, _) = await GetAsync($"/api/documents/{doc.DocumentId}/versions/1/passages?focus={Guid.NewGuid()}");

        await Assert.That(foreignStatus).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(foreignBody.GetProperty("code").GetString()).IsEqualTo("documents.passage_not_found");
        await Assert.That(unknownStatus).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    [Arguments("Failed")]
    [Arguments("Queued")]
    public async Task Passages_ofUnfinishedVersion_returnEmptyItems(string versionStatus)
    {
        var doc = await Seeder.AddDocumentAsync(new("Unfinished", $"owner-{Guid.NewGuid():N}", VersionStatus: versionStatus));

        var (status, body) = await GetAsync($"/api/documents/{doc.DocumentId}/versions/1/passages");

        await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body.GetProperty("items").GetArrayLength()).IsEqualTo(0);
        await Assert.That(body.GetProperty("isActive").GetBoolean()).IsFalse();
    }

    [Test]
    public async Task Passages_ofSupersededVersion_areReturned()
    {
        var doc = await Seeder.AddDocumentAsync(new("Old", $"owner-{Guid.NewGuid():N}", VersionStatus: "Superseded", Passages: 3));

        var (_, body) = await GetAsync($"/api/documents/{doc.DocumentId}/versions/1/passages");

        await Assert.That(body.GetProperty("items").GetArrayLength()).IsEqualTo(3);
        await Assert.That(body.GetProperty("versionStatus").GetString()).IsEqualTo("superseded");
    }

    [Test]
    public async Task VersionsAndPassages_unknownDocumentOrVersion_return404WithCodes()
    {
        var doc = await Seeder.AddDocumentAsync(new("One", $"owner-{Guid.NewGuid():N}"));

        var (versionsStatus, versionsBody) = await GetAsync($"/api/documents/{Guid.NewGuid()}/versions");
        var (passagesStatus, passagesBody) = await GetAsync($"/api/documents/{Guid.NewGuid()}/versions/1/passages");
        var (versionStatus, versionBody) = await GetAsync($"/api/documents/{doc.DocumentId}/versions/9/passages");

        await Assert.That(versionsStatus).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(versionsBody.GetProperty("code").GetString()).IsEqualTo("documents.not_found");
        await Assert.That(passagesStatus).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(passagesBody.GetProperty("code").GetString()).IsEqualTo("documents.not_found");
        await Assert.That(versionStatus).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(versionBody.GetProperty("code").GetString()).IsEqualTo("documents.version_not_found");
    }

    [Test]
    public async Task Passages_invalidPaging_returns400()
    {
        var doc = await Seeder.AddDocumentAsync(new("One", $"owner-{Guid.NewGuid():N}"));

        var (status, body) = await GetAsync($"/api/documents/{doc.DocumentId}/versions/1/passages?pageSize=101&focus=nope");

        await Assert.That(status).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(body.GetProperty("errors").TryGetProperty("pageSize", out _)).IsTrue();
        await Assert.That(body.GetProperty("errors").TryGetProperty("focus", out _)).IsTrue();
    }

    [Test]
    public async Task ReadEndpoints_asAnonymous_return401()
    {
        var id = Guid.NewGuid();
        foreach (var url in new[] { "/api/documents/facets", $"/api/documents/{id}/versions", $"/api/documents/{id}/versions/1/passages" })
        {
            var (status, _) = await GetAsync(url, Persona.Anonymous);
            await Assert.That(status).IsEqualTo(HttpStatusCode.Unauthorized);
        }
    }

    [Test]
    [Arguments(Persona.KnowledgeSeeker)]
    [Arguments(Persona.ContentOwner)]
    [Arguments(Persona.Maintainer)]
    public async Task ReadEndpoints_asEveryRole_return200(Persona persona)
    {
        var doc = await Seeder.AddDocumentAsync(new("One", $"owner-{Guid.NewGuid():N}", Passages: 1));

        foreach (var url in new[] { "/api/documents/facets", $"/api/documents/{doc.DocumentId}/versions", $"/api/documents/{doc.DocumentId}/versions/1/passages" })
        {
            var (status, _) = await GetAsync(url, persona);
            await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
        }
    }
}
