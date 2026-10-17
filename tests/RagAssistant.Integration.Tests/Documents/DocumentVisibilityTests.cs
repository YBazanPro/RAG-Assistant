using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RagAssistant.Integration.Tests.Fixtures;
using static RagAssistant.Integration.Tests.Documents.LifecycleHelpers;

namespace RagAssistant.Integration.Tests.Documents;

// Spec 016: private and shared documents, each Documents endpoint with an owner and somebody else.
[ClassDataSource<RagAssistantAppFactory>(Shared = SharedType.PerTestSession)]
public sealed class DocumentVisibilityTests(RagAssistantAppFactory factory)
{
    private const string Owner = "vis-owner";
    private const string Other = "vis-other";
    private const string Maintainer = "vis-maintainer";

    private HttpClient OwnerClient() => factory.CreateClientAs(Persona.ContentOwner, subject: Owner);

    private HttpClient OtherClient(Persona persona = Persona.ContentOwner) => factory.CreateClientAs(persona, subject: Other);

    private HttpClient MaintainerClient() => factory.CreateClientAs(Persona.Maintainer, subject: Maintainer);

    private static async Task<(Guid Id, byte[] Content)> ImportAsync(
        HttpClient client, string? visibility, string[]? tags = null, string? ownerName = null, byte[]? content = null)
    {
        content ??= DocumentUploads.UniqueMarkdown();
        var fields = new List<(string, string)>();
        if (visibility is not null)
        {
            fields.Add(("visibility", visibility));
        }

        foreach (var tag in tags ?? [])
        {
            fields.Add(("tags", tag));
        }

        using var form = DocumentUploads.Form("policy.md", content, DocumentUploads.UniqueTitle(), ownerName ?? "Finance", [.. fields]);
        var response = await client.PostAsync("/api/documents", form);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("documentId").GetGuid(), content);
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, Guid id)
    {
        var response = await client.GetAsync($"/api/documents/{id}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<string> EtagAsync(HttpClient client, Guid id)
    {
        var response = await client.GetAsync($"/api/documents/{id}");
        return response.Headers.ETag!.ToString();
    }

    private static object Body(string title, string? visibility) => new
    {
        title,
        ownerName = "Finance",
        language = "en",
        tags = Array.Empty<string>(),
        effectiveDate = (string?)null,
        source = (string?)null,
        visibility,
    };

    // AC-2
    [Test]
    public async Task Import_withoutVisibility_isShared_andPrivateIsOwnedByTheCaller()
    {
        using var owner = OwnerClient();
        var (shared, _) = await ImportAsync(owner, null);
        var (priv, _) = await ImportAsync(owner, "private");

        var sharedBody = await GetAsync(owner, shared);
        var privateBody = await GetAsync(owner, priv);

        await Assert.That(sharedBody.GetProperty("visibility").GetString()).IsEqualTo("shared");
        await Assert.That(privateBody.GetProperty("visibility").GetString()).IsEqualTo("private");
        await Assert.That(privateBody.GetProperty("isOwner").GetBoolean()).IsTrue();
        await Assert.That(privateBody.GetProperty("createdBy").GetString()).IsEqualTo(Owner);
    }

    // AC-2
    [Test]
    public async Task Import_withUnknownVisibility_returns400()
    {
        using var owner = OwnerClient();
        using var form = DocumentUploads.Form("policy.md", DocumentUploads.UniqueMarkdown(), DocumentUploads.UniqueTitle(), "Finance", ("visibility", "secret"));

        var response = await owner.PostAsync("/api/documents", form);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    // AC-5
    [Test]
    public async Task List_hidesPrivateDocumentsFromEveryoneButTheOwner()
    {
        using var owner = OwnerClient();
        var (priv, _) = await ImportAsync(owner, "private", ownerName: "Visibility List");

        var seenByOwner = await owner.GetFromJsonAsync<JsonElement>("/api/documents?owner=Visibility%20List&pageSize=100");
        var mine = await owner.GetFromJsonAsync<JsonElement>("/api/documents?visibility=mine&pageSize=100");
        using var other = OtherClient();
        var seenByOther = await other.GetFromJsonAsync<JsonElement>("/api/documents?owner=Visibility%20List&pageSize=100");
        using var maintainer = MaintainerClient();
        var seenByMaintainer = await maintainer.GetFromJsonAsync<JsonElement>("/api/documents?owner=Visibility%20List&pageSize=100");

        var ownerRow = seenByOwner.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == priv);
        await Assert.That(ownerRow.GetProperty("visibility").GetString()).IsEqualTo("private");
        await Assert.That(ownerRow.GetProperty("isOwner").GetBoolean()).IsTrue();
        await Assert.That(mine.GetProperty("items").EnumerateArray().Any(i => i.GetProperty("id").GetGuid() == priv)).IsTrue();
        await Assert.That(seenByOther.GetProperty("totalCount").GetInt32()).IsEqualTo(0);
        await Assert.That(seenByMaintainer.GetProperty("totalCount").GetInt32()).IsEqualTo(0);
    }

    // AC-6
    [Test]
    public async Task Facets_ignorePrivateDocumentsOfOtherPeople()
    {
        using var owner = OwnerClient();
        var tag = $"vis-{Guid.NewGuid():N}";
        await ImportAsync(owner, "private", tags: [tag], ownerName: tag);

        var forOwner = (await owner.GetStringAsync("/api/documents/facets"));
        using var other = OtherClient();
        var forOther = (await other.GetStringAsync("/api/documents/facets"));

        await Assert.That(forOwner).Contains(tag);
        await Assert.That(forOther).DoesNotContain(tag);
    }

    // AC-7
    [Test]
    public async Task Get_privateDocument_isNotFoundForOthers_withTheBodyOfAnUnknownId()
    {
        using var owner = OwnerClient();
        var (priv, _) = await ImportAsync(owner, "private");
        using var other = OtherClient(Persona.KnowledgeSeeker);

        var denied = await other.GetAsync($"/api/documents/{priv}");
        var unknown = await other.GetAsync($"/api/documents/{Guid.NewGuid()}");
        var ownerView = await owner.GetAsync($"/api/documents/{priv}");

        await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await CodeAsync(denied)).IsEqualTo(await CodeAsync(unknown));
        await Assert.That(ownerView.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    // AC-7
    [Test]
    public async Task Get_sharedDocument_returnsCreatedByOnlyToItsOwner()
    {
        using var owner = OwnerClient();
        var (shared, _) = await ImportAsync(owner, "shared");
        using var other = OtherClient(Persona.KnowledgeSeeker);

        var forOther = await GetAsync(other, shared);
        var forOwner = await GetAsync(owner, shared);

        await Assert.That(forOther.GetProperty("createdBy").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(forOther.GetProperty("isOwner").GetBoolean()).IsFalse();
        await Assert.That(forOwner.GetProperty("createdBy").GetString()).IsEqualTo(Owner);
    }

    // AC-8
    [Test]
    [Arguments("versions")]
    [Arguments("versions/1/passages")]
    [Arguments("versions/1/file")]
    public async Task ReadSubresources_ofAPrivateDocument_areNotFoundForOthers(string path)
    {
        using var owner = OwnerClient();
        var (priv, _) = await ImportAsync(owner, "private");
        using var other = OtherClient(Persona.KnowledgeSeeker);
        using var maintainer = MaintainerClient();

        var asOwner = await owner.GetAsync($"/api/documents/{priv}/{path}");
        var asOther = await other.GetAsync($"/api/documents/{priv}/{path}");
        var asMaintainer = await maintainer.GetAsync($"/api/documents/{priv}/{path}");

        await Assert.That(asOwner.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(asOther.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(asMaintainer.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    // AC-9
    [Test]
    [Arguments("archive")]
    [Arguments("restore")]
    [Arguments("reprocess")]
    [Arguments("ingestion/retry")]
    public async Task Actions_onAPrivateDocument_areNotFoundForOthers(string action)
    {
        using var owner = OwnerClient();
        var (priv, _) = await ImportAsync(owner, "private");
        using var other = OtherClient();

        var response = await PostActionAsync(other, priv, action);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((await GetAsync(owner, priv)).GetProperty("status").GetString()).IsNotEqualTo("archived");
    }

    // AC-9
    [Test]
    public async Task PutVersionsAndEvents_onAPrivateDocument_areNotFoundForOthers()
    {
        using var owner = OwnerClient();
        var (priv, _) = await ImportAsync(owner, "private");
        using var other = OtherClient();
        var etag = await EtagAsync(owner, priv);

        var put = await PutAsync(other, priv, Body("Hijacked", null), etag);
        var upload = await UploadVersionAsync(other, priv, Markdown());
        var events = await other.GetAsync($"/api/documents/{priv}/ingestion/events");

        await Assert.That(put.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(upload.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(events.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((await GetAsync(owner, priv)).GetProperty("title").GetString()).IsNotEqualTo("Hijacked");
    }

    // AC-16, AC-17
    [Test]
    public async Task Switch_toPrivateAndBack_hidesAndRestoresTheDocumentForOthers()
    {
        using var owner = OwnerClient();
        using var other = OtherClient(Persona.KnowledgeSeeker);
        var (id, _) = await ImportAsync(owner, "shared");
        var before = await GetAsync(owner, id);
        var revision = before.GetProperty("revision").GetInt32();
        var title = before.GetProperty("title").GetString()!;

        var toPrivate = await PutAsync(owner, id, Body(title, "private"), await EtagAsync(owner, id));
        var hidden = await other.GetAsync($"/api/documents/{id}");
        var toShared = await PutAsync(owner, id, Body(title, "shared"), await EtagAsync(owner, id));
        var visible = await other.GetAsync($"/api/documents/{id}");

        await Assert.That(toPrivate.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await JsonAsync(toPrivate)).GetProperty("revision").GetInt32()).IsEqualTo(revision + 1);
        await Assert.That(hidden.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(toShared.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(visible.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    // AC-18
    [Test]
    public async Task Switch_byAnotherContentOwner_isForbidden_butAnUnchangedValueIsAccepted()
    {
        using var owner = OwnerClient();
        using var other = OtherClient();
        var (id, _) = await ImportAsync(owner, "shared");
        var title = (await GetAsync(owner, id)).GetProperty("title").GetString()!;

        var changed = await PutAsync(other, id, Body(title, "private"), await EtagAsync(other, id));
        var unchanged = await PutAsync(other, id, Body(title + " edited", "shared"), await EtagAsync(other, id));
        var omitted = await PutAsync(other, id, Body(title + " again", null), await EtagAsync(other, id));

        await Assert.That(changed.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await CodeAsync(changed)).IsEqualTo("documents.visibility_owner_only");
        await Assert.That(unchanged.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(omitted.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await GetAsync(owner, id)).GetProperty("visibility").GetString()).IsEqualTo("shared");
    }

    // AC-19
    [Test]
    public async Task Switch_withAStaleIfMatch_isRejectedAndLeavesVisibilityAlone()
    {
        using var owner = OwnerClient();
        var (id, _) = await ImportAsync(owner, "shared");
        var title = (await GetAsync(owner, id)).GetProperty("title").GetString()!;

        var response = await PutAsync(owner, id, Body(title, "private"), "\"99\"");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.PreconditionFailed);
        await Assert.That((await GetAsync(owner, id)).GetProperty("visibility").GetString()).IsEqualTo("shared");
    }

    [Test]
    public async Task Put_withUnknownVisibility_returns400()
    {
        using var owner = OwnerClient();
        var (id, _) = await ImportAsync(owner, "shared");

        var response = await PutAsync(owner, id, Body("Title", "secret"), await EtagAsync(owner, id));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    // AC-4
    [Test]
    public async Task Import_ofContentHeldAsSomeoneElsesPrivateDocument_succeeds_butASharedDuplicateIsRefused()
    {
        using var owner = OwnerClient();
        using var other = OtherClient();
        var (_, privateContent) = await ImportAsync(owner, "private");
        var (_, sharedContent) = await ImportAsync(owner, "shared");

        using var privateCopy = DocumentUploads.Form("copy.md", privateContent, DocumentUploads.UniqueTitle());
        using var sharedCopy = DocumentUploads.Form("copy.md", sharedContent, DocumentUploads.UniqueTitle());
        var allowed = await other.PostAsync("/api/documents", privateCopy);
        var refused = await other.PostAsync("/api/documents", sharedCopy);

        await Assert.That(allowed.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(await CodeAsync(refused)).IsEqualTo("documents.duplicate_content");
    }

    // AC-4: no hint that somebody else holds the same file privately, and the owner's own duplicate is still refused
    [Test]
    public async Task Import_ofTheOwnersOwnPrivateContent_isRefusedWithAReferenceToIt()
    {
        using var owner = OwnerClient();
        var (id, content) = await ImportAsync(owner, "private");

        using var again = DocumentUploads.Form("again.md", content, DocumentUploads.UniqueTitle());
        var response = await owner.PostAsync("/api/documents", again);
        var body = await JsonAsync(response);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(body.GetProperty("existingDocumentId").GetGuid()).IsEqualTo(id);
    }
}
