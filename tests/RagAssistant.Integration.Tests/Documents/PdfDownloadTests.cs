using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RagAssistant.Integration.Tests.Fixtures;

namespace RagAssistant.Integration.Tests.Documents;

// Spec 010, AC-23 and AC-24: opening the original of a version.
[ClassDataSource<RagAssistantAppFactory>(Shared = SharedType.PerTestSession)]
public sealed class PdfDownloadTests(RagAssistantAppFactory factory)
{
    private NpgsqlDataSource Db => factory.Services.GetRequiredService<NpgsqlDataSource>();

    private static string Route(Guid documentId, int number = 1) => $"/api/documents/{documentId}/versions/{number}/file";

    private async Task<(Guid DocumentId, byte[] Bytes)> ImportPdfAsync(string fileName = "report.pdf")
    {
        var pdf = TestPdfs.WithText();
        using var client = factory.CreateClientAs(Persona.ContentOwner);
        using var form = TestPdfs.Form(pdf, fileName);
        using var response = await client.PostAsync("/api/documents", form);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("documentId").GetGuid(), pdf);
    }

    [Test]
    public async Task Download_pdfAsASeeker_returnsTheExactBytesWithSafeHeaders()
    {
        var (id, pdf) = await ImportPdfAsync();
        using var client = factory.CreateClientAs(Persona.KnowledgeSeeker);

        using var response = await client.GetAsync(Route(id));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await response.Content.ReadAsByteArrayAsync()).IsEquivalentTo(pdf);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("application/pdf");
        await Assert.That(response.Content.Headers.ContentDisposition?.DispositionType).IsEqualTo("inline");
        await Assert.That(response.Content.Headers.ContentDisposition?.FileNameStar).IsEqualTo("report.pdf");
        await Assert.That(response.Headers.GetValues("X-Content-Type-Options").Single()).IsEqualTo("nosniff");
        await Assert.That(response.Headers.GetValues("Content-Security-Policy").Single()).IsEqualTo("default-src 'none'; sandbox");
        await Assert.That(response.Headers.CacheControl?.NoStore).IsTrue();
        await Assert.That(response.Headers.CacheControl?.Private).IsTrue();
        await Assert.That(response.Headers.AcceptRanges.Contains("bytes")).IsTrue();
    }

    [Test]
    public async Task Download_rangeRequest_returns206WithThoseBytes()
    {
        var (id, pdf) = await ImportPdfAsync();
        using var client = factory.CreateClientAs(Persona.KnowledgeSeeker);
        using var request = new HttpRequestMessage(HttpMethod.Get, Route(id)) { Headers = { Range = new RangeHeaderValue(0, 99) } };

        using var response = await client.SendAsync(request);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.PartialContent);
        await Assert.That(await response.Content.ReadAsByteArrayAsync()).IsEquivalentTo(pdf.Take(100).ToArray());
        await Assert.That(response.Content.Headers.ContentRange?.ToString()).IsEqualTo($"bytes 0-99/{pdf.Length}");
    }

    [Test]
    public async Task Download_filenameWithSpacesAndAccents_isEncodedInTheHeader()
    {
        var (id, _) = await ImportPdfAsync("Résumé 2026.pdf");
        using var client = factory.CreateClientAs(Persona.KnowledgeSeeker);

        using var response = await client.GetAsync(Route(id));

        await Assert.That(string.Join(",", response.Content.Headers.GetValues("Content-Disposition")))
            .IsEqualTo("inline; filename*=UTF-8''R%C3%A9sum%C3%A9%202026.pdf");
    }

    [Test]
    public async Task Download_archivedDocument_isStillAllowed()
    {
        var (id, pdf) = await ImportPdfAsync();
        using var owner = factory.CreateClientAs(Persona.ContentOwner);
        using var archived = await owner.PostAsync($"/api/documents/{id}/archive", null);
        await Assert.That(archived.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var seeker = factory.CreateClientAs(Persona.KnowledgeSeeker);

        using var response = await seeker.GetAsync(Route(id));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await response.Content.ReadAsByteArrayAsync()).Length).IsEqualTo(pdf.Length);
    }

    [Test]
    public async Task Download_textVersion_returnsTheNormalizedContentAsAnAttachment()
    {
        var markdown = $"# Title {Guid.NewGuid()}\r\n\r\nBody line.\r\n";
        using var owner = factory.CreateClientAs(Persona.ContentOwner);
        using var created = await owner.PostAsync("/api/documents", new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(markdown)), "file", "notes.md" }, { new StringContent(DocumentUploads.UniqueTitle()), "title" }, { new StringContent("Finance"), "ownerName" } });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("documentId").GetGuid();
        using var seeker = factory.CreateClientAs(Persona.KnowledgeSeeker);

        using var response = await seeker.GetAsync(Route(id));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentType?.ToString()).IsEqualTo("text/plain; charset=utf-8");
        await Assert.That(response.Content.Headers.ContentDisposition?.DispositionType).IsEqualTo("attachment");
        await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo(markdown.Replace("\r\n", "\n", StringComparison.Ordinal));
        await Assert.That(response.Headers.GetValues("X-Content-Type-Options").Single()).IsEqualTo("nosniff");
    }

    [Test]
    public async Task Download_unknownDocument_returns404NotFound()
    {
        using var client = factory.CreateClientAs(Persona.KnowledgeSeeker);

        using var response = await client.GetAsync(Route(Guid.NewGuid()));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()).IsEqualTo("documents.not_found");
    }

    [Test]
    public async Task Download_unknownVersion_returns404VersionNotFound()
    {
        var (id, _) = await ImportPdfAsync();
        using var client = factory.CreateClientAs(Persona.KnowledgeSeeker);

        using var response = await client.GetAsync(Route(id, number: 9));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()).IsEqualTo("documents.version_not_found");
    }

    [Test]
    public async Task Download_blobMissingFromTheStore_returns404FileMissing()
    {
        var (id, _) = await ImportPdfAsync();
        var key = await DocumentUploads.ScalarAsync<string>(Db, "SELECT storage_key FROM documents.document_versions WHERE document_id = $1", id);
        File.Delete(Path.Combine(factory.StoragePath, key!.Replace('/', Path.DirectorySeparatorChar)));
        using var client = factory.CreateClientAs(Persona.KnowledgeSeeker);

        using var response = await client.GetAsync(Route(id));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()).IsEqualTo("documents.file_missing");
        await Assert.That(factory.Logs.Entries.Any(e => e.Message.Contains("is missing from the store", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Download_anonymous_returns401()
    {
        var (id, _) = await ImportPdfAsync();
        using var client = factory.CreateClientAs(Persona.Anonymous);

        using var response = await client.GetAsync(Route(id));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }
}
