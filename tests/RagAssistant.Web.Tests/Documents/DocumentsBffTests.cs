using System.Net;
using System.Text.Json;
using RagAssistant.Web.Tests.Fixtures;

namespace RagAssistant.Web.Tests.Documents;

// Spec 008, AC-19 (what the server can prove about prerendering) and AC-34 (the BFF forwards the new routes and the SSE stream).
[ClassDataSource<WebAppFactory>(Shared = SharedType.PerTestSession)]
public class DocumentsBffTests(WebAppFactory factory)
{
    [Test]
    public async Task Proxy_forwardsTheReadRoutesWithTheSessionsBearerToken()
    {
        var client = await factory.CreateSignedInAsync(Persona.KnowledgeSeeker);
        var id = Guid.NewGuid();
        string[] paths = ["/api/documents", "/api/documents/facets", $"/api/documents/{id}/versions", $"/api/documents/{id}/versions/2/passages"];

        foreach (var path in paths)
        {
            var response = await client.GetAsync(new Uri(path + "?page=1", UriKind.Relative));
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        await Assert.That(factory.Api.AuthorizationFor($"/api/documents/{id}/versions")).IsEqualTo("Bearer access-token-KnowledgeSeeker");
        await Assert.That(factory.Api.AuthorizationFor($"/api/documents/{id}/versions/2/passages")).IsEqualTo("Bearer access-token-KnowledgeSeeker");
    }

    [Test]
    public async Task Proxy_deliversIngestionEventsIncrementally()
    {
        var id = Guid.NewGuid();
        var client = await factory.CreateSignedInAsync(Persona.ContentOwner);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        using var response = await client.GetAsync(
            new Uri($"/api/documents/{id}/ingestion/events", UriKind.Relative), HttpCompletionOption.ResponseHeadersRead, cts.Token);
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        // The upstream is held open until released, so seeing the first event now proves nothing is buffered.
        var first = new List<string?> { await reader.ReadLineAsync(cts.Token), await reader.ReadLineAsync(cts.Token), await reader.ReadLineAsync(cts.Token) };
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("text/event-stream");
        await Assert.That(first[0]).IsEqualTo("id: 1");
        await Assert.That(first[1]).IsEqualTo("event: ingestion.stage");
        using var data = JsonDocument.Parse(first[2]!["data: ".Length..]);
        await Assert.That(data.RootElement.GetProperty("stage").GetString()).IsEqualTo("extracting");

        factory.Api.ReleaseIngestion(id);
        await reader.ReadLineAsync(cts.Token);
        await Assert.That(await reader.ReadLineAsync(cts.Token)).IsEqualTo("id: 2");
    }

    [Test]
    public async Task DetailPage_prerender_rendersTheShellWithoutCallingTheHost()
    {
        var id = Guid.NewGuid();
        var client = await factory.CreateSignedInAsync(Persona.KnowledgeSeeker);

        var html = await client.GetStringAsync(new Uri($"/documents/{id}", UriKind.Relative));

        await Assert.That(html).Contains("Loading document");
        await Assert.That(html).Contains("<title>Document · RagAssistant</title>");
        await Assert.That(factory.Api.Received($"/api/documents/{id}")).IsFalse();
        await Assert.That(factory.Api.Received($"/api/documents/{id}/versions")).IsFalse();
    }

    [Test]
    public async Task ListPage_prerender_rendersTheShellAndLoadingState()
    {
        var client = await factory.CreateSignedInAsync(Persona.KnowledgeSeeker);

        var html = await client.GetStringAsync(new Uri("/documents?q=prerender-shell", UriKind.Relative));

        await Assert.That(html).Contains("Loading documents");
        await Assert.That(html).Contains("<h1>Documents</h1>");
        await Assert.That(html).DoesNotContain("<table");
    }
}
