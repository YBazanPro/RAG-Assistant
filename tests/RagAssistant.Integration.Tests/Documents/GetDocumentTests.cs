using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RagAssistant.Integration.Tests.Fixtures;

namespace RagAssistant.Integration.Tests.Documents;

// Spec 006, AC-14, AC-15 and the read half of AC-16.
[ClassDataSource<RagAssistantAppFactory>(Shared = SharedType.PerTestSession)]
public sealed class GetDocumentTests(RagAssistantAppFactory factory)
{
    private async Task<(Guid DocumentId, Guid VersionId)> ImportAsync()
    {
        using var client = factory.CreateClientAs(Persona.ContentOwner);
        using var form = DocumentUploads.Form("budget.md", DocumentUploads.UniqueMarkdown(), DocumentUploads.UniqueTitle());
        var response = await client.PostAsync("/api/documents", form);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("documentId").GetGuid(), body.GetProperty("versionId").GetGuid());
    }

    [Test]
    public async Task Get_importedDocument_returnsQueuedDetailForKnowledgeSeeker()
    {
        var (documentId, versionId) = await ImportAsync();
        using var client = factory.CreateClientAs(Persona.KnowledgeSeeker);

        var response = await client.GetAsync($"/api/documents/{documentId}");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body.GetProperty("id").GetGuid()).IsEqualTo(documentId);
        await Assert.That(body.GetProperty("ownerName").GetString()).IsEqualTo("Finance");
        await Assert.That(body.GetProperty("status").GetString()).IsEqualTo("queued");
        await Assert.That(body.GetProperty("activeVersionId").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(body.GetProperty("createdBy").GetString()).IsEqualTo("test-ContentOwner");

        var latest = body.GetProperty("latestVersion");
        await Assert.That(latest.GetProperty("id").GetGuid()).IsEqualTo(versionId);
        await Assert.That(latest.GetProperty("number").GetInt32()).IsEqualTo(1);
        await Assert.That(latest.GetProperty("status").GetString()).IsEqualTo("queued");
        await Assert.That(latest.GetProperty("sourceFormat").GetString()).IsEqualTo("markdown");
        await Assert.That(latest.GetProperty("originalFileName").GetString()).IsEqualTo("budget.md");
        await Assert.That(latest.TryGetProperty("content", out _)).IsFalse();
    }

    [Test]
    public async Task Get_unknownId_returns404WithCode()
    {
        using var client = factory.CreateClientAs(Persona.KnowledgeSeeker);

        var response = await client.GetAsync($"/api/documents/{Guid.NewGuid()}");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(body.GetProperty("code").GetString()).IsEqualTo("documents.not_found");
    }

    [Test]
    public async Task Get_asAnonymous_returns401()
    {
        using var client = factory.CreateClientAs(Persona.Anonymous);

        var response = await client.GetAsync($"/api/documents/{Guid.NewGuid()}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    [Arguments(Persona.ContentOwner)]
    [Arguments(Persona.Maintainer)]
    public async Task Get_asHigherRoles_returns200(Persona persona)
    {
        var (documentId, _) = await ImportAsync();
        using var client = factory.CreateClientAs(persona);

        var response = await client.GetAsync($"/api/documents/{documentId}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }
}
