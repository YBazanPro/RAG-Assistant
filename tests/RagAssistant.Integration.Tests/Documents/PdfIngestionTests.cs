using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RagAssistant.Integration.Tests.Fixtures;
using RagAssistant.Modules.Documents.Tests;
using static RagAssistant.Integration.Tests.Documents.LifecycleHelpers;
using static RagAssistant.Modules.Documents.Tests.TestPdf;

namespace RagAssistant.Integration.Tests.Documents;

// Spec 010, AC-15, AC-16, AC-18 and AC-19: PDFs through the ingestion pipeline, end to end.
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerTestSession)]
[ParallelLimiter<IngestionParallelLimit>]
public sealed class PdfIngestionTests(PostgresFixture postgres)
{
    private static string Sentence(string tag, int i) => $"{tag} sentence number {i} talks about budget approvals and travel limits.";

    /// <summary>A page of body text: enough lines for the page to hold several passages worth of text.</summary>
    private static PageSpec TextPage(string tag, int lines = 30) =>
        Page(Body(700, [.. Enumerable.Range(0, lines).Select(i => Sentence(tag, i))]));

    private static async Task<ImportedDocument> ImportPdfAsync(IngestionHarness app, byte[] pdf, Persona persona = Persona.ContentOwner)
    {
        using var client = app.CreateClientAs(persona);
        using var form = TestPdfs.Form(pdf);
        using var response = await client.PostAsync("/api/documents", form);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new(body.GetProperty("documentId").GetGuid(), body.GetProperty("versionId").GetGuid(), body.GetProperty("ingestionRunId").GetGuid());
    }

    private static Task<string?> ErrorCodeAsync(IngestionHarness app, Guid runId) =>
        app.ScalarAsync<string>("SELECT error_code FROM documents.ingestion_runs WHERE id = $1", runId);

    private static Task<string?> ExtractingMessageAsync(IngestionHarness app, Guid runId) =>
        app.ScalarAsync<string>(
            "SELECT message FROM documents.ingestion_events WHERE run_id = $1 AND stage = 'Extracting' AND status = 'Completed'", runId);

    private static Task<List<object?[]>> PassagePagesAsync(IngestionHarness app, Guid versionId) =>
        app.RowsAsync("SELECT page_start, page_end FROM documents.passages WHERE version_id = $1 ORDER BY position", versionId);

    [Test]
    public async Task Ingest_textPdf_becomesIndexedWithPagesOnItsPassages()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var pdf = Create(TextPage("Alpha"), TextPage("Beta"), TextPage("Gamma"));

        var doc = await ImportPdfAsync(app, pdf);
        await app.WaitForRunAsync(doc.RunId, "Succeeded");

        await Assert.That(await app.ScalarAsync<string>("SELECT status FROM documents.document_versions WHERE id = $1", doc.VersionId)).IsEqualTo("Indexed");
        await Assert.That(await app.ScalarAsync<Guid?>("SELECT active_version_id FROM documents.documents WHERE id = $1", doc.DocumentId)).IsEqualTo(doc.VersionId);
        var pages = await PassagePagesAsync(app, doc.VersionId);
        await Assert.That(pages.Count).IsGreaterThan(3);
        await Assert.That(pages.All(p => p[0] is int start && p[1] is int end && start >= 1 && end <= 3 && start <= end)).IsTrue();
        await Assert.That(pages[0][0]).IsEqualTo(1);
        await Assert.That(pages[^1][1]).IsEqualTo(3);
        await Assert.That(await ExtractingMessageAsync(app, doc.RunId)).IsEqualTo("3 pages, 0 without text, 0 margin lines removed");
        await Assert.That(await app.ScalarAsync<long>(
            "SELECT count(*) FROM documents.document_versions WHERE id = $1 AND pages_without_text IS NOT NULL", doc.VersionId)).IsEqualTo(0L);
    }

    [Test]
    public async Task Ingest_pdfPassagesAreEmbeddedFromTheSectionPathAndText()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var doc = await ImportPdfAsync(app, Create(TextPage("Delta", lines: 6)));

        await app.WaitForRunAsync(doc.RunId, "Succeeded");

        var texts = (await app.RowsAsync("SELECT text FROM documents.passages WHERE version_id = $1", doc.VersionId)).Select(r => (string)r[0]!).ToList();
        await Assert.That(texts.Count).IsEqualTo(1);
        await Assert.That(app.Generator.Batches.SelectMany(b => b).Any(input => input.Contains("Delta sentence number 5", StringComparison.Ordinal))).IsTrue();
        await Assert.That(texts[0]).Contains("Delta sentence number 0 talks about budget approvals and travel limits. Delta sentence number 1");
    }

    [Test]
    public async Task Ingest_pdfWithTextlessPages_isIndexedAndRecordsThem()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var pages = Enumerable.Range(0, 8).Select(i => TextPage($"Page{i}", lines: 4)).Concat([ScannedPage(), ScannedPage()]);

        var doc = await ImportPdfAsync(app, Create(pages));
        await app.WaitForRunAsync(doc.RunId, "Succeeded");

        await Assert.That(await app.ScalarAsync<int[]>("SELECT pages_without_text FROM documents.document_versions WHERE id = $1", doc.VersionId))
            .IsEquivalentTo([9, 10]);
        await Assert.That(await ExtractingMessageAsync(app, doc.RunId)).IsEqualTo("10 pages, 2 without text, 0 margin lines removed");
    }

    [Test]
    public async Task Ingest_scanOnlyInLaterPages_failsRequiresOcrWhenTheFullPassCounts()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        // The probe samples 20 of 40 pages and sees 2 text pages (10%); the full pass counts 2 of 40 (5%).
        var pages = new[] { TextPage("First", 4), TextPage("Second", 4) }.Concat(Enumerable.Range(0, 38).Select(_ => ScannedPage()));

        var doc = await ImportPdfAsync(app, Create(pages));
        await app.WaitForRunAsync(doc.RunId, "Failed");

        await Assert.That(await ErrorCodeAsync(app, doc.RunId)).IsEqualTo("documents.requires_ocr");
        await Assert.That(await app.ScalarAsync<long>("SELECT count(*) FROM documents.passages WHERE version_id = $1", doc.VersionId)).IsEqualTo(0L);
    }

    [Test]
    [Arguments("encrypted", "documents.pdf_encrypted")]
    [Arguments("corrupt", "documents.pdf_invalid")]
    public async Task Ingest_unreadablePdf_failsWithAContentFreeCode(string kind, string code)
    {
        await using var app = await IngestionHarness.StartAsync(postgres, settings: NoRetries);
        var doc = await ImportPdfAsync(app, kind == "encrypted" ? Encrypted() : Corrupt());

        await app.WaitForRunAsync(doc.RunId, "Failed");

        await Assert.That(await ErrorCodeAsync(app, doc.RunId)).IsEqualTo(code);
        await Assert.That(await app.ScalarAsync<string>("SELECT status FROM documents.document_versions WHERE id = $1", doc.VersionId)).IsEqualTo("Failed");
        var message = await app.ScalarAsync<string>("SELECT error_message FROM documents.ingestion_runs WHERE id = $1", doc.RunId);
        await Assert.That(message).DoesNotContain("this is not a pdf body");
    }

    [Test]
    public async Task Ingest_moreThanMaxPages_failsBeforeExtracting()
    {
        var settings = new Dictionary<string, string> { ["Documents:Pdf:MaxPages"] = "2" };
        await using var app = await IngestionHarness.StartAsync(postgres, settings: settings);

        var doc = await ImportPdfAsync(app, Create(TextPage("A", 3), TextPage("B", 3), TextPage("C", 3)));
        await app.WaitForRunAsync(doc.RunId, "Failed");

        await Assert.That(await ErrorCodeAsync(app, doc.RunId)).IsEqualTo("documents.pdf_too_many_pages");
    }

    [Test]
    public async Task Ingest_extractionOverTheTimeout_failsWithExtractionTimeout()
    {
        var settings = new Dictionary<string, string> { ["Documents:Pdf:ExtractionTimeout"] = "00:00:00.001" };
        await using var app = await IngestionHarness.StartAsync(postgres, settings: settings);

        var doc = await ImportPdfAsync(app, Create(Enumerable.Range(0, 30).Select(i => TextPage($"Slow{i}", 20))));
        await app.WaitForRunAsync(doc.RunId, "Failed");

        await Assert.That(await ErrorCodeAsync(app, doc.RunId)).IsEqualTo("documents.extraction_timeout");
    }

    [Test]
    public async Task Ingest_blobMissingWhenTheRunStarts_failsWithFileMissing()
    {
        await using var app = await StartWithoutEmbeddingsAsync(postgres);
        var doc = await ImportPdfAsync(app, Create(TextPage("Gone", 4)));
        var key = await app.ScalarAsync<string>("SELECT storage_key FROM documents.document_versions WHERE id = $1", doc.VersionId);
        File.Delete(Path.Combine(app.StoragePath, key!.Replace('/', Path.DirectorySeparatorChar)));

        app.Generator.Available = true;
        await app.WaitForRunAsync(doc.RunId, "Failed");

        await Assert.That(await ErrorCodeAsync(app, doc.RunId)).IsEqualTo("documents.file_missing");
        await Assert.That(await app.ScalarAsync<string>(
            "SELECT stage FROM documents.ingestion_events WHERE run_id = $1 AND status = 'Failed'", doc.RunId)).IsEqualTo("Extracting");
    }

    [Test]
    public async Task UploadVersion_pdfOverMarkdownAndBack_isIndexedAndSupersedesThePreviousVersion()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var doc = await ImportIndexedAsync(app);
        using var client = app.CreateClientAs(Persona.ContentOwner);

        using var pdfForm = new MultipartFormDataContent { { new ByteArrayContent(Create(TextPage("Switch", 6))), "file", "switch.pdf" } };
        using var toPdf = await client.PostAsync($"/api/documents/{doc.DocumentId}/versions", pdfForm);
        await Assert.That(toPdf.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        await app.WaitForRunAsync((await JsonAsync(toPdf)).GetProperty("ingestionRunId").GetGuid(), "Succeeded");

        var back = await UploadVersionAsync(client, doc.DocumentId, Markdown("Back to markdown, long enough to index."));
        await Assert.That(back.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        await app.WaitForRunAsync((await JsonAsync(back)).GetProperty("ingestionRunId").GetGuid(), "Succeeded");

        var versions = await app.RowsAsync(
            "SELECT number, source_format, status, content IS NULL, storage_key IS NULL FROM documents.document_versions WHERE document_id = $1 ORDER BY number",
            doc.DocumentId);
        await Assert.That(versions.Select(v => $"{v[0]}:{v[1]}:{v[2]}:{v[3]}:{v[4]}").ToArray())
            .IsEquivalentTo(["1:Markdown:Superseded:False:True", "2:Pdf:Superseded:True:False", "3:Markdown:Indexed:False:True"]);
    }

    [Test]
    public async Task Reprocess_pdf_reusesTheBlobAndReExtracts()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var doc = await ImportPdfAsync(app, Create(TextPage("Again", 6)));
        await app.WaitForRunAsync(doc.RunId, "Succeeded");
        using var client = app.CreateClientAs(Persona.ContentOwner);

        var response = await PostActionAsync(client, doc.DocumentId, "reprocess");
        var json = await JsonAsync(response);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        await app.WaitForRunAsync(json.GetProperty("ingestionRunId").GetGuid(), "Succeeded");

        var versions = await app.RowsAsync(
            "SELECT origin, storage_key, content_hash, status, passage_count FROM documents.document_versions WHERE document_id = $1 ORDER BY number", doc.DocumentId);
        await Assert.That(versions[1][0]).IsEqualTo("Reprocess");
        await Assert.That(versions[1][1]).IsEqualTo(versions[0][1]);
        await Assert.That(versions[1][2]).IsEqualTo(versions[0][2]);
        await Assert.That(versions.Select(v => (string)v[3]!).ToArray()).IsEquivalentTo(["Superseded", "Indexed"]);
        var pages = await PassagePagesAsync(app, json.GetProperty("versionId").GetGuid());
        await Assert.That(pages.Count).IsGreaterThan(0);
        await Assert.That(pages.All(p => p[0] is 1)).IsTrue();
        await Assert.That(Directory.GetFiles(Path.Combine(app.StoragePath, "pdf")).Length).IsEqualTo(1);
    }
}
