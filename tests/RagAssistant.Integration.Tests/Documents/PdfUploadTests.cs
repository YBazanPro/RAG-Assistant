using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RagAssistant.Integration.Tests.Fixtures;

namespace RagAssistant.Integration.Tests.Documents;

// Spec 010, AC-1 to AC-6 and AC-26: uploading PDFs.
[ClassDataSource<RagAssistantAppFactory>(Shared = SharedType.PerTestSession)]
public sealed class PdfUploadTests(RagAssistantAppFactory factory)
{
    private const int OneMiB = 1_048_576;

    private NpgsqlDataSource Db => factory.Services.GetRequiredService<NpgsqlDataSource>();

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private string BlobPath(byte[] pdf) => Path.Combine(factory.StoragePath, "pdf", $"{Sha256(pdf)}.pdf");

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(
        MultipartFormDataContent form, Persona persona = Persona.ContentOwner, WebApplicationFactory<Program>? app = null)
    {
        using var client = (app ?? factory).CreateClientAs(persona);
        using (form)
        {
            using var response = await client.PostAsync("/api/documents", form);
            return (response.StatusCode, response.Content.Headers.ContentLength is 0 ? default : await BodyAsync(response));
        }
    }

    /// <summary>A host with its own store, so a test can assert that nothing at all was left behind.</summary>
    private (WebApplicationFactory<Program> App, string Path) IsolatedHost(params (string Key, string Value)[] settings)
    {
        var path = Path.Combine(Path.GetTempPath(), "ragassistant-tests", $"pdf-{Guid.NewGuid():N}");
        var app = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Documents:Storage:Path", path);
            foreach (var (key, value) in settings)
            {
                b.UseSetting(key, value);
            }
        });
        return (app, path);
    }

    private static int FileCount(string directory) => Directory.Exists(directory) ? Directory.GetFiles(directory).Length : 0;

    private async Task<long> DocumentsWithTitleAsync(string title) =>
        await DocumentUploads.ScalarAsync<long>(Db, "SELECT count(*) FROM documents.documents WHERE title = $1", title);

    [Test]
    public async Task Import_validPdf_returns202AndStoresTheFileAndAPdfVersion()
    {
        var pdf = TestPdfs.WithText();
        var title = DocumentUploads.UniqueTitle();

        var (status, body) = await PostAsync(TestPdfs.Form(pdf, "Annual Report.pdf", title));

        await Assert.That(status).IsEqualTo(HttpStatusCode.Accepted);
        var documentId = body.GetProperty("documentId").GetGuid();
        var row = await DocumentUploads.ScalarAsync<string>(
            Db,
            "SELECT source_format || '|' || coalesce(content, '<null>') || '|' || storage_key || '|' || content_hash || '|' || size_bytes || '|' || " +
            "original_file_name || '|' || status FROM documents.document_versions WHERE document_id = $1",
            documentId);
        var hash = Sha256(pdf);
        await Assert.That(row).IsEqualTo($"Pdf|<null>|pdf/{hash}.pdf|{hash}|{pdf.Length}|Annual Report.pdf|Queued");
        await Assert.That(await DocumentUploads.ScalarAsync<string>(
            Db, "SELECT latest_content_hash FROM documents.documents WHERE id = $1", documentId)).IsEqualTo(hash);
        await Assert.That(await File.ReadAllBytesAsync(BlobPath(pdf))).IsEquivalentTo(pdf);
        await Assert.That(await DocumentUploads.ScalarAsync<long>(
            Db,
            "SELECT count(*) FROM documents.ingestion_runs r JOIN documents.document_versions v ON v.id = r.version_id WHERE v.document_id = $1 AND r.status = 'Queued'",
            documentId)).IsEqualTo(1L);
    }

    [Test]
    public async Task Import_uppercaseExtension_isAccepted()
    {
        var (status, _) = await PostAsync(TestPdfs.Form(TestPdfs.WithText(), "REPORT.PDF"));

        await Assert.That(status).IsEqualTo(HttpStatusCode.Accepted);
    }

    [Test]
    public async Task Import_pdfSlightlyOverTheTextLimit_isAccepted()
    {
        var (status, _) = await PostAsync(TestPdfs.Form(TestPdfs.Padded(OneMiB + 1024)));

        await Assert.That(status).IsEqualTo(HttpStatusCode.Accepted);
    }

    [Test]
    public async Task Import_textFileOverTheTextLimit_returns400FileTooLarge()
    {
        var (status, body) = await PostAsync(DocumentUploads.Form("big.md", new byte[OneMiB + 1]));

        await Assert.That(status).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(body.GetProperty("code").GetString()).IsEqualTo("documents.file_too_large");
    }

    [Test]
    public async Task Import_pdfOverThePdfLimit_returns400AndLeavesNothingBehind()
    {
        var (app, path) = IsolatedHost(("Documents:Import:MaxPdfSizeBytes", (2 * OneMiB).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        await using var _ = app;
        var title = DocumentUploads.UniqueTitle();
        try
        {
            var (status, body) = await PostAsync(TestPdfs.Form(TestPdfs.Padded(2 * OneMiB + 1), title: title), app: app);

            await Assert.That(status).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(body.GetProperty("code").GetString()).IsEqualTo("documents.file_too_large");
            await Assert.That(body.GetProperty("errors").GetProperty("file")[0].GetString()).Contains("2097152");
            await Assert.That(await DocumentsWithTitleAsync(title)).IsEqualTo(0L);
            await Assert.That(FileCount(Path.Combine(path, "tmp"))).IsEqualTo(0);
            await Assert.That(FileCount(Path.Combine(path, "pdf"))).IsEqualTo(0);
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Test]
    public async Task Import_pdfExactlyAtThePdfLimit_isAccepted()
    {
        var (app, path) = IsolatedHost(("Documents:Import:MaxPdfSizeBytes", (2 * OneMiB).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        await using var _ = app;
        try
        {
            var (status, _) = await PostAsync(TestPdfs.Form(TestPdfs.Padded(2 * OneMiB)), app: app);

            await Assert.That(status).IsEqualTo(HttpStatusCode.Accepted);
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Test]
    public async Task Import_pdfExtensionButNotAPdf_returns400InvalidPdfAndPersistsNothing()
    {
        var (app, path) = IsolatedHost();
        await using var _ = app;
        var title = DocumentUploads.UniqueTitle();
        try
        {
            var (status, body) = await PostAsync(TestPdfs.Form("# just markdown\n"u8.ToArray(), "fake.pdf", title), app: app);

            await Assert.That(status).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(body.GetProperty("code").GetString()).IsEqualTo("documents.invalid_pdf");
            await Assert.That(body.GetProperty("errors").GetProperty("file").GetArrayLength()).IsEqualTo(1);
            await Assert.That(await DocumentsWithTitleAsync(title)).IsEqualTo(0L);
            await Assert.That(FileCount(Path.Combine(path, "tmp"))).IsEqualTo(0);
            await Assert.That(FileCount(Path.Combine(path, "pdf"))).IsEqualTo(0);
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Test]
    public async Task Import_emptyPdfFile_returns400InvalidPdf()
    {
        var (status, body) = await PostAsync(TestPdfs.Form([]));

        await Assert.That(status).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(body.GetProperty("code").GetString()).IsEqualTo("documents.invalid_pdf");
    }

    [Test]
    public async Task Import_samePdfTwice_returns409WithTheExistingDocumentAndKeepsOneBlob()
    {
        var pdf = TestPdfs.WithText();
        var (firstStatus, first) = await PostAsync(TestPdfs.Form(pdf));

        var (secondStatus, second) = await PostAsync(TestPdfs.Form(pdf, title: DocumentUploads.UniqueTitle()));

        await Assert.That(firstStatus).IsEqualTo(HttpStatusCode.Accepted);
        await Assert.That(secondStatus).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(second.GetProperty("code").GetString()).IsEqualTo("documents.duplicate_content");
        await Assert.That(second.GetProperty("existingDocumentId").GetGuid()).IsEqualTo(first.GetProperty("documentId").GetGuid());
        await Assert.That(File.Exists(BlobPath(pdf))).IsTrue();
        await Assert.That(Directory.GetFiles(Path.Combine(factory.StoragePath, "pdf"), $"{Sha256(pdf)}*").Length).IsEqualTo(1);
    }

    [Test]
    public async Task Import_twoIdenticalPdfsAtOnce_oneAcceptedAndOneConflict()
    {
        var pdf = TestPdfs.WithText();

        var results = await Task.WhenAll(PostAsync(TestPdfs.Form(pdf)), PostAsync(TestPdfs.Form(pdf)));

        var statuses = results.Select(r => r.Status).Order().ToArray();
        await Assert.That(statuses).IsEquivalentTo(new[] { HttpStatusCode.Accepted, HttpStatusCode.Conflict });
        await Assert.That(Directory.GetFiles(Path.Combine(factory.StoragePath, "pdf"), $"{Sha256(pdf)}*").Length).IsEqualTo(1);
    }

    [Test]
    public async Task Import_samePdfAsAnArchivedDocument_isAcceptedAndReusesTheBlob()
    {
        var pdf = TestPdfs.WithText();
        var (_, first) = await PostAsync(TestPdfs.Form(pdf));
        using (var client = factory.CreateClientAs(Persona.ContentOwner))
        {
            using var archived = await client.PostAsync($"/api/documents/{first.GetProperty("documentId").GetGuid()}/archive", null);
            await Assert.That(archived.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        var (status, second) = await PostAsync(TestPdfs.Form(pdf));

        await Assert.That(status).IsEqualTo(HttpStatusCode.Accepted);
        await Assert.That(second.GetProperty("documentId").GetGuid()).IsNotEqualTo(first.GetProperty("documentId").GetGuid());
        await Assert.That(Directory.GetFiles(Path.Combine(factory.StoragePath, "pdf"), $"{Sha256(pdf)}*").Length).IsEqualTo(1);
    }

    [Test]
    public async Task Import_fileSentBeforeTheFields_givesTheSameResult()
    {
        // DocumentUploads.Form puts the file first; TestPdfs.Form sends the fields after it. Here the fields come first.
        var pdf = TestPdfs.WithText();
        var title = DocumentUploads.UniqueTitle();
        var form = new MultipartFormDataContent
        {
            { new StringContent(title), "title" },
            { new StringContent("Finance"), "ownerName" },
        };
        form.Add(new ByteArrayContent(pdf), "file", "ordered.pdf");

        var (status, _) = await PostAsync(form);

        await Assert.That(status).IsEqualTo(HttpStatusCode.Accepted);
        await Assert.That(await DocumentsWithTitleAsync(title)).IsEqualTo(1L);
    }

    [Test]
    public async Task Import_fieldsAfterAnUnsupportedFile_stillReportTheFileError()
    {
        var (status, body) = await PostAsync(DocumentUploads.Form("sheet.xlsx", [1, 2, 3]));

        await Assert.That(status).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(body.GetProperty("code").GetString()).IsEqualTo("documents.unsupported_extension");
    }

    [Test]
    public async Task Import_bodyIsNotMultipart_returns400()
    {
        using var client = factory.CreateClientAs(Persona.ContentOwner);
        using var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

        using var response = await client.PostAsync("/api/documents", content);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Import_multipartBodyCutOffEarly_returns400NotAServerError()
    {
        using var client = factory.CreateClientAs(Persona.ContentOwner);
        using var content = new StringContent("--cut\r\nContent-Disposition: form-data; name=\"title\"\r\n\r\nabc");
        content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("multipart/form-data; boundary=cut");

        using var response = await client.PostAsync("/api/documents", content);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Import_clientDisconnectsMidUpload_leavesNoTempFileAndNoDocument()
    {
        var (app, path) = IsolatedHost();
        await using var _ = app;
        var title = DocumentUploads.UniqueTitle();
        try
        {
            using var client = app.CreateClientAs(Persona.ContentOwner);
            using var cts = new CancellationTokenSource();
            using var form = new MultipartFormDataContent
            {
                { new StringContent(title), "title" },
                { new StringContent("Finance"), "ownerName" },
            };
            form.Add(new TestPdfs.GeneratedPdfContent(8L * OneMiB, cts), "file", "vanishing.pdf");

            await Assert.That(async () => await client.PostAsync("/api/documents", form, cts.Token)).Throws<OperationCanceledException>();

            // The server notices the abort asynchronously.
            for (var i = 0; i < 50 && FileCount(Path.Combine(path, "tmp")) > 0; i++)
            {
                await Task.Delay(100);
            }

            await Assert.That(FileCount(Path.Combine(path, "tmp"))).IsEqualTo(0);
            await Assert.That(FileCount(Path.Combine(path, "pdf"))).IsEqualTo(0);
            await Assert.That(await DocumentsWithTitleAsync(title)).IsEqualTo(0L);
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Test]
    [NotInParallel]
    public async Task Import_largePdf_isStreamedWithoutBufferingTheWholeFile()
    {
        const long fileSize = 40L * OneMiB;

        // The in-process test server allocates a lot per request body byte on its own (about 4x here), so the bound is relative to a
        // baseline that only drains the same body: the upload code may add a few buffers on top, but never a copy of the file.
        await using var baselineHost = factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddTransient<IStartupFilter, DrainBodyFilter>()));
        using var baselineClient = baselineHost.CreateClient();
        using var client = factory.CreateClientAs(Persona.ContentOwner);
        await MeasureAsync(baselineClient, "/drain", fileSize); // warm up the pools and JIT
        var baseline = await MeasureAsync(baselineClient, "/drain", fileSize);

        var (status, allocated) = await MeasureUploadAsync(client, fileSize);

        await Assert.That(status).IsEqualTo(HttpStatusCode.Accepted);
        await Assert.That(allocated - baseline).IsLessThan(fileSize / 4);
    }

    private static async Task<long> MeasureAsync(HttpClient client, string route, long fileSize) =>
        (await MeasureUploadAsync(client, fileSize, route)).Allocated;

    private static async Task<(HttpStatusCode Status, long Allocated)> MeasureUploadAsync(HttpClient client, long fileSize, string route = "/api/documents")
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(DocumentUploads.UniqueTitle()), "title" },
            { new StringContent("Finance"), "ownerName" },
        };
        form.Add(new TestPdfs.GeneratedPdfContent(fileSize), "file", "large.pdf");
        GC.Collect();
        var before = GC.GetTotalAllocatedBytes(precise: true);

        using var response = await client.PostAsync(route, form);

        return (response.StatusCode, GC.GetTotalAllocatedBytes(precise: true) - before);
    }

    /// <summary>Answers /drain by reading and discarding the request body, nothing else.</summary>
    private sealed class DrainBodyFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Path != "/drain")
                {
                    await nextMiddleware();
                    return;
                }

                var buffer = new byte[81920];
                while (await context.Request.Body.ReadAsync(buffer) > 0)
                {
                }

                context.Response.StatusCode = StatusCodes.Status200OK;
            });
            next(app);
        };
    }

    [Test]
    public async Task Import_pdfAsAKnowledgeSeeker_returns403AndStoresNothing()
    {
        var pdf = TestPdfs.WithText();

        var (status, _) = await PostAsync(TestPdfs.Form(pdf), Persona.KnowledgeSeeker);

        await Assert.That(status).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(File.Exists(BlobPath(pdf))).IsFalse();
    }

    [Test]
    public async Task Import_pdfAsAnonymous_returns401()
    {
        var (status, _) = await PostAsync(TestPdfs.Form(TestPdfs.WithText()), Persona.Anonymous);

        await Assert.That(status).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Import_pdfAsAMaintainer_returns202()
    {
        var (status, _) = await PostAsync(TestPdfs.Form(TestPdfs.WithText()), Persona.Maintainer);

        await Assert.That(status).IsEqualTo(HttpStatusCode.Accepted);
    }

    [Test]
    public async Task UploadVersion_pdfOverATextDocument_returns202AndStoresThePdfVersion()
    {
        var (_, created) = await PostAsync(DocumentUploads.Form("first.md", DocumentUploads.UniqueMarkdown()));
        var documentId = created.GetProperty("documentId").GetGuid();
        // The first run is stuck queued in this factory, which the guard treats as in progress, so finish it by hand.
        await using (var command = Db.CreateCommand(
            "UPDATE documents.document_versions SET status = 'Indexed' WHERE document_id = $1"))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = documentId });
            await command.ExecuteNonQueryAsync();
        }

        var pdf = TestPdfs.WithText();
        using var client = factory.CreateClientAs(Persona.ContentOwner);
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(pdf), "file", "second.pdf");
        using var response = await client.PostAsync($"/api/documents/{documentId}/versions", form);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        var row = await DocumentUploads.ScalarAsync<string>(
            Db,
            "SELECT source_format || '|' || coalesce(content, '<null>') || '|' || storage_key FROM documents.document_versions " +
            "WHERE document_id = $1 AND number = 2",
            documentId);
        await Assert.That(row).IsEqualTo($"Pdf|<null>|pdf/{Sha256(pdf)}.pdf");
        await Assert.That(await DocumentUploads.ScalarAsync<string>(
            Db, "SELECT latest_content_hash FROM documents.documents WHERE id = $1", documentId)).IsEqualTo(Sha256(pdf));
        await Assert.That(File.Exists(BlobPath(pdf))).IsTrue();
    }

    [Test]
    public async Task UploadVersion_invalidPdf_returns400AndLeavesTheDocumentAlone()
    {
        var (_, created) = await PostAsync(DocumentUploads.Form("first.md", DocumentUploads.UniqueMarkdown()));
        var documentId = created.GetProperty("documentId").GetGuid();
        await using (var command = Db.CreateCommand("UPDATE documents.document_versions SET status = 'Indexed' WHERE document_id = $1"))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = documentId });
            await command.ExecuteNonQueryAsync();
        }

        using var client = factory.CreateClientAs(Persona.ContentOwner);
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent("not a pdf"u8.ToArray()), "file", "second.pdf");
        using var response = await client.PostAsync($"/api/documents/{documentId}/versions", form);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await BodyAsync(response)).GetProperty("code").GetString()).IsEqualTo("documents.invalid_pdf");
        await Assert.That(await DocumentUploads.ScalarAsync<long>(
            Db, "SELECT count(*) FROM documents.document_versions WHERE document_id = $1", documentId)).IsEqualTo(1L);
    }
}
