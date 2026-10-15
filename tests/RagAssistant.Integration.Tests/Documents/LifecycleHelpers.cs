using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using RagAssistant.Integration.Tests.Fixtures;

namespace RagAssistant.Integration.Tests.Documents;

/// <summary>Requests and seed steps shared by the spec 009 lifecycle tests.</summary>
internal static class LifecycleHelpers
{
    public static readonly Dictionary<string, string> NoRetries = new() { ["Documents:Ingestion:EmbeddingMaxRetries"] = "1" };

    public static object ValidBody(string title = "Budget Exception Policy") => new
    {
        title,
        ownerName = "Finance",
        language = "en",
        tags = new[] { "budget", "finance" },
        effectiveDate = "2026-01-01",
        source = (string?)null,
    };

    public static async Task<HttpResponseMessage> PutAsync(HttpClient client, Guid documentId, object body, string? ifMatch)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/documents/{documentId}") { Content = JsonContent.Create(body) };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await client.SendAsync(request);
    }

    public static async Task<HttpResponseMessage> UploadVersionAsync(HttpClient client, Guid documentId, byte[]? bytes, string fileName = "next.md")
    {
        using var form = DocumentUploads.Form(fileName, bytes, title: null, ownerName: null);
        return await client.PostAsync($"/api/documents/{documentId}/versions", form);
    }

    public static Task<HttpResponseMessage> PostActionAsync(HttpClient client, Guid documentId, string action) =>
        client.PostAsync($"/api/documents/{documentId}/{action}", null);

    public static async Task<ImportedDocument> ImportIndexedAsync(IngestionHarness app, string? markdown = null)
    {
        var doc = await app.ImportAsync(markdown);
        await app.WaitForRunAsync(doc.RunId, "Succeeded");
        return doc;
    }

    /// <summary>An import whose run stays queued because the embedding model is unreachable.</summary>
    public static async Task<IngestionHarness> StartWithoutEmbeddingsAsync(PostgresFixture postgres) =>
        await IngestionHarness.StartAsync(postgres, generator: new FakeEmbeddingGenerator { Available = false });

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    public static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await JsonAsync(response)).GetProperty("code").GetString();

    public static byte[] Markdown(string? body = null) => Encoding.UTF8.GetBytes(IngestionHarness.UniqueMarkdown(body ?? "Revised policy text that is long enough to index."));

    public static async Task<string?> DocumentStatusAsync(IngestionHarness app, Guid documentId) =>
        await app.ScalarAsync<string>("SELECT status FROM documents.documents WHERE id = $1", documentId);

    public static async Task<long> CountAsync(IngestionHarness app, string table, Guid documentId) =>
        await app.ScalarAsync<long>(
            table == "passages"
                ? "SELECT count(*) FROM documents.passages p JOIN documents.document_versions v ON v.id = p.version_id WHERE v.document_id = $1"
                : $"SELECT count(*) FROM documents.{table} WHERE document_id = $1",
            documentId);
}
