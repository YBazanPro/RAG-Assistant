using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using RagAssistant.Web.Client.Documents;

namespace RagAssistant.Web.Tests.Documents;

// Spec 010, AC-28 to AC-33: PDF import in the UI (upload dialog, progress, passages with pages, links to the original).
public class PdfUiTests
{
    private static (BunitContext Ctx, FakeDocumentsApi Api, FakeEventSource Events) NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddFluentUIComponents();
        var api = new FakeDocumentsApi();
        var events = new FakeEventSource();
        ctx.Services.AddSingleton<IDocumentsApi>(api);
        ctx.Services.AddSingleton<IFileUploader>(api.Uploader);
        ctx.Services.AddSingleton<IIngestionEventSource>(events);
        ctx.Services.AddSingleton<TimeProvider>(new ManualTimeProvider());
        return (ctx, api, events);
    }

    private static IRenderedComponent<DocumentUpload> RenderImport(BunitContext ctx) =>
        ctx.Render<DocumentUpload>(p => p.Add(c => c.Open, true));

    private static void Choose(IRenderedComponent<DocumentUpload> cut, string name, int size = 20) =>
        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary(new byte[size], name));

    private static void Submit(IRenderedComponent<DocumentUpload> cut) =>
        cut.FindAll("fluent-button").First(b => b.TextContent.Trim().EndsWith("Import", StringComparison.Ordinal)).Click();

    private static void CancelUpload(IRenderedComponent<DocumentUpload> cut) =>
        cut.FindAll("fluent-button").First(b => b.TextContent.Trim() == "Cancel upload").Click();

    // AC-28
    [Test]
    public async Task Import_pdfFile_isAcceptedAndSentThroughTheUploader()
    {
        var (ctx, api, _) = NewContext();
        using var _c = ctx;
        var cut = RenderImport(ctx);
        Choose(cut, "Annual Report.pdf", size: 5_000_000);
        await cut.SetTextAsync("Owner", "Finance");

        Submit(cut);

        cut.WaitForState(() => api.Uploader.Requests.Count == 1, TimeSpan.FromSeconds(5));
        var request = api.Uploader.Requests.Single();
        await Assert.That(request.Url).IsEqualTo("api/documents");
        await Assert.That(request.Fields.First(f => f.Key == "title").Value).IsEqualTo("Annual Report");
        await Assert.That(request.Headers["X-Requested-With"]).IsEqualTo("ragassistant");
    }

    [Test]
    public async Task Import_pdfUpToTheLimit_isNotRefusedOnTheClient()
    {
        var (ctx, api, _) = NewContext();
        using var _c = ctx;
        var cut = RenderImport(ctx);
        Choose(cut, "big.pdf", size: 2_000_000);
        await cut.SetTextAsync("Owner", "Finance");

        Submit(cut);

        cut.WaitForState(() => api.Uploader.Requests.Count == 1, TimeSpan.FromSeconds(5));
        await Assert.That(cut.FindAll("#up-file-error").Count).IsEqualTo(0);
    }

    // AC-29
    [Test]
    public async Task Upload_inProgress_showsAProgressbarWithItsValues()
    {
        var (ctx, api, _) = NewContext();
        using var _c = ctx;
        var gate = new TaskCompletionSource();
        api.Uploader.Gate = gate.Task;
        api.Uploader.Steps.Add(new UploadProgress(25L * 1024 * 1024, 100L * 1024 * 1024));
        var cut = RenderImport(ctx);
        Choose(cut, "big.pdf");
        await cut.SetTextAsync("Owner", "Finance");

        Submit(cut);
        cut.WaitForState(() => cut.FindAll("progress[role=progressbar]").Count == 1, TimeSpan.FromSeconds(5));

        var bar = cut.Find("progress[role=progressbar]");
        await Assert.That(bar.GetAttribute("aria-valuenow")).IsEqualTo("25");
        await Assert.That(bar.GetAttribute("aria-valuemin")).IsEqualTo("0");
        await Assert.That(bar.GetAttribute("aria-valuemax")).IsEqualTo("100");
        await Assert.That(cut.Markup).Contains("Uploading 25 MiB of 100 MiB (25%)");
        gate.SetResult();
    }

    [Test]
    public async Task Upload_allBytesSentButNoAnswerYet_saysTheFileIsBeingChecked()
    {
        var (ctx, api, _) = NewContext();
        using var _c = ctx;
        var gate = new TaskCompletionSource();
        api.Uploader.Gate = gate.Task;
        api.Uploader.Steps.Add(new UploadProgress(1000, 1000));
        var cut = RenderImport(ctx);
        Choose(cut, "scan.pdf");
        await cut.SetTextAsync("Owner", "Finance");

        Submit(cut);

        cut.WaitForState(() => cut.Markup.Contains("Checking PDF", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        await Assert.That(cut.Find("progress[role=progressbar]").GetAttribute("aria-valuenow")).IsEqualTo("100");
        gate.SetResult();
    }

    [Test]
    public async Task Cancel_whileUploading_abortsAndReturnsToIdleKeepingTheValues()
    {
        var (ctx, api, _) = NewContext();
        using var _c = ctx;
        var gate = new TaskCompletionSource();
        api.Uploader.Gate = gate.Task;
        var cut = RenderImport(ctx);
        Choose(cut, "big.pdf");
        await cut.SetTextAsync("Owner", "Finance");
        Submit(cut);
        cut.WaitForState(() => cut.FindAll("progress[role=progressbar]").Count == 1, TimeSpan.FromSeconds(5));

        CancelUpload(cut);

        cut.WaitForState(() => cut.FindAll("progress[role=progressbar]").Count == 0, TimeSpan.FromSeconds(5));
        await Assert.That(cut.TextValue("Owner")).IsEqualTo("Finance");
        await Assert.That(cut.TextValue("Title")).IsEqualTo("big");
        await Assert.That(cut.FindAll("fluent-message-bar").Count).IsEqualTo(0);
        await Assert.That(cut.TextInput("Owner").Instance.Disabled).IsFalse();
        await Assert.That(cut.Markup).Contains("big.pdf");
    }

    [Test]
    [Arguments("documents.invalid_pdf", "The file is not a PDF document.")]
    [Arguments("documents.requires_ocr", "This PDF has no text layer (it looks scanned). OCR is not supported yet.")]
    [Arguments("documents.text_layer_unusable", "The text layer of this PDF is not readable text.")]
    public async Task Upload_rejectedPdf_showsTheMessageUnderTheFilePicker(string code, string message)
    {
        var (ctx, api, _) = NewContext();
        using var _c = ctx;
        api.Uploader.OnUpload = _ => FakeFileUploader.Failure(new ApiError(
            400, code, message, new Dictionary<string, string[]> { ["file"] = [message] }));
        var cut = RenderImport(ctx);
        Choose(cut, "scan.pdf");
        await cut.SetTextAsync("Owner", "Finance");

        Submit(cut);

        cut.WaitForState(() => cut.FindAll("#up-file-error").Count == 1, TimeSpan.FromSeconds(5));
        await Assert.That(cut.Find("#up-file-error").TextContent).Contains(message);
        await Assert.That(cut.TextValue("Owner")).IsEqualTo("Finance");
    }

    [Test]
    public async Task Upload_browserNoLongerHasTheFile_asksToChooseItAgain()
    {
        var (ctx, api, _) = NewContext();
        using var _c = ctx;
        api.Uploader.OnUpload = _ => new UploadResult(UploadResult.NoFile, null);
        var cut = RenderImport(ctx);
        Choose(cut, "gone.pdf");
        await cut.SetTextAsync("Owner", "Finance");

        Submit(cut);

        cut.WaitForState(() => cut.FindAll("#up-file-error").Count == 1, TimeSpan.FromSeconds(5));
        await Assert.That(cut.Find("#up-file-error").TextContent).Contains("Choose it again");
    }

    [Test]
    public async Task Dispose_whileUploading_abortsTheUpload()
    {
        var (ctx, api, _) = NewContext();
        using var _c = ctx;
        var gate = new TaskCompletionSource();
        api.Uploader.Gate = gate.Task;
        var cut = RenderImport(ctx);
        Choose(cut, "big.pdf");
        await cut.SetTextAsync("Owner", "Finance");
        Submit(cut);
        cut.WaitForState(() => api.Uploader.Requests.Count == 1, TimeSpan.FromSeconds(5));

        // Navigating away disposes the component; the upload must not carry on with nobody to tell.
        await ctx.DisposeComponentsAsync();

        for (var i = 0; i < 100 && api.Uploader.Aborted == 0; i++)
        {
            await Task.Delay(20);
        }

        await Assert.That(api.Uploader.Aborted).IsEqualTo(1);
    }

    // AC-30
    [Test]
    public async Task IngestionStatus_progressEvents_showADeterminateBarAndLabel()
    {
        var (ctx, _, events) = NewContext();
        using var _c = ctx;
        events.EnqueueEvents(Samples.Stage("embedding", "started", "1"), Samples.Progress("embedding", "2", 120, 540)).EnqueueHang();

        var cut = ctx.Render<IngestionStatus>(p => p
            .Add(c => c.Run, Samples.Run("running", "embedding"))
            .Add(c => c.DocumentId, Samples.DocId)
            .Add(c => c.Live, true));

        cut.WaitForState(() => cut.Markup.Contains("Embedding 120 of 540 passages", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        var bar = cut.Find("progress[role=progressbar]");
        await Assert.That(bar.GetAttribute("aria-valuenow")).IsEqualTo("120");
        await Assert.That(bar.GetAttribute("aria-valuemax")).IsEqualTo("540");
        await Assert.That(bar.GetAttribute("aria-valuemin")).IsEqualTo("0");
    }

    [Test]
    public async Task IngestionStatus_extractionProgress_countsPages()
    {
        var (ctx, _, events) = NewContext();
        using var _c = ctx;
        events.EnqueueEvents(Samples.Stage("extracting", "started", "1"), Samples.Progress("extracting", "2", 20, 200)).EnqueueHang();

        var cut = ctx.Render<IngestionStatus>(p => p
            .Add(c => c.Run, Samples.Run("running", "extracting"))
            .Add(c => c.DocumentId, Samples.DocId)
            .Add(c => c.Live, true));

        cut.WaitForState(() => cut.Markup.Contains("Extracting 20 of 200 pages", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task IngestionStatus_eventsWithoutCounters_keepTheStepperOnly()
    {
        var (ctx, _, events) = NewContext();
        using var _c = ctx;
        events.EnqueueEvents(Samples.Stage("chunking", "started", "1")).EnqueueHang();

        var cut = ctx.Render<IngestionStatus>(p => p
            .Add(c => c.Run, Samples.Run("running", "extracting"))
            .Add(c => c.DocumentId, Samples.DocId)
            .Add(c => c.Live, true));

        cut.WaitForState(() => cut.FindAll(".step-current").Any(s => s.TextContent.Contains("Chunking", StringComparison.Ordinal)), TimeSpan.FromSeconds(5));
        await Assert.That(cut.FindAll("progress").Count).IsEqualTo(0);
    }

    [Test]
    public async Task IngestionStatus_aStageThatCompletes_dropsItsProgressBar()
    {
        var (ctx, _, events) = NewContext();
        using var _c = ctx;
        events.EnqueueEvents(
            Samples.Progress("embedding", "1", 120, 540),
            Samples.Stage("embedding", "completed", "2"),
            Samples.Stage("storing", "started", "3")).EnqueueHang();

        var cut = ctx.Render<IngestionStatus>(p => p
            .Add(c => c.Run, Samples.Run("running", "embedding"))
            .Add(c => c.DocumentId, Samples.DocId)
            .Add(c => c.Live, true));

        cut.WaitForState(() => cut.FindAll(".step-current").Any(s => s.TextContent.Contains("Storing", StringComparison.Ordinal)), TimeSpan.FromSeconds(5));
        await Assert.That(cut.FindAll("progress").Count).IsEqualTo(0);
    }

    // AC-31
    [Test]
    public async Task PassageCard_pdfPassageWithAPageRange_showsPagesAndAnOpenPageLink()
    {
        var (ctx, _, _) = NewContext();
        using var _c = ctx;
        var id = Guid.NewGuid();

        var cut = ctx.Render<PassageCard>(p => p
            .Add(c => c.Passage, Samples.Passage(0) with { PageStart = 12, PageEnd = 13 })
            .Add(c => c.DocumentId, id)
            .Add(c => c.VersionNumber, 2));

        await Assert.That(cut.Find(".passage-pages").TextContent.Trim()).IsEqualTo("pp. 12–13");
        var link = cut.Find("a.open-page-link");
        await Assert.That(link.TextContent.Trim()).IsEqualTo("Open page 12");
        await Assert.That(link.GetAttribute("href")).IsEqualTo($"api/documents/{id}/versions/2/file#page=12");
        await Assert.That(link.GetAttribute("target")).IsEqualTo("_blank");
        await Assert.That(link.GetAttribute("rel")).IsEqualTo("noopener");
    }

    [Test]
    public async Task PassageCard_singlePage_saysPNotPp()
    {
        var (ctx, _, _) = NewContext();
        using var _c = ctx;

        var cut = ctx.Render<PassageCard>(p => p
            .Add(c => c.Passage, Samples.Passage(0) with { PageStart = 7, PageEnd = 7 })
            .Add(c => c.DocumentId, Guid.NewGuid())
            .Add(c => c.VersionNumber, 1));

        await Assert.That(cut.Find(".passage-pages").TextContent.Trim()).IsEqualTo("p. 7");
        await Assert.That(cut.Find("a.open-page-link").TextContent.Trim()).IsEqualTo("Open page 7");
    }

    [Test]
    public async Task PassageCard_textPassage_showsNeitherPagesNorALink()
    {
        var (ctx, _, _) = NewContext();
        using var _c = ctx;

        var cut = ctx.Render<PassageCard>(p => p
            .Add(c => c.Passage, Samples.Passage(0))
            .Add(c => c.DocumentId, Guid.NewGuid())
            .Add(c => c.VersionNumber, 1));

        await Assert.That(cut.FindAll(".passage-pages").Count).IsEqualTo(0);
        await Assert.That(cut.FindAll("a.open-page-link").Count).IsEqualTo(0);
    }

    // AC-32
    [Test]
    public async Task VersionHistory_pdfVersion_showsFormatSizeAndALinkToTheOriginal()
    {
        var (ctx, _, _) = NewContext();
        using var _c = ctx;
        var id = Guid.NewGuid();
        var versions = new[]
        {
            Samples.Version(2, isActive: true) with { SourceFormat = "pdf", OriginalFileName = "report.pdf", SizeBytes = 5 * 1024 * 1024 },
            Samples.Version(1) with { SourceFormat = "markdown" },
        };

        var cut = ctx.Render<VersionHistory>(p => p.Add(c => c.Versions, versions).Add(c => c.DocumentId, id));

        await Assert.That(cut.Markup).Contains(">PDF<");
        await Assert.That(cut.Markup).Contains("5 MiB");
        var open = cut.Find($"a.original-file-link[href='api/documents/{id}/versions/2/file']");
        await Assert.That(open.TextContent.Trim()).IsEqualTo("Open original");
        await Assert.That(open.GetAttribute("target")).IsEqualTo("_blank");
        var download = cut.Find($"a.original-file-link[href='api/documents/{id}/versions/1/file']");
        await Assert.That(download.TextContent.Trim()).IsEqualTo("Download original");
        await Assert.That(cut.Markup).Contains(">Markdown<");
    }

    [Test]
    public async Task VersionHistory_versionWithTextlessPages_warnsWhichPagesAreNotSearchable()
    {
        var (ctx, _, _) = NewContext();
        using var _c = ctx;
        var versions = new[] { Samples.Version(1, isActive: true) with { SourceFormat = "pdf", PagesWithoutText = [31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 45] } };

        var cut = ctx.Render<VersionHistory>(p => p.Add(c => c.Versions, versions).Add(c => c.DocumentId, Guid.NewGuid()));

        await Assert.That(cut.Find(".version-warning").TextContent.Trim())
            .IsEqualTo("Pages 31–40, 45 have no extractable text and are not searchable.");
    }

    [Test]
    public async Task VersionHistory_failedPdfRun_showsTheRunsMessage()
    {
        var (ctx, _, _) = NewContext();
        using var _c = ctx;
        var failed = Samples.Run("failed", "extracting", error: "The PDF is password-protected.");
        var versions = new[] { Samples.Version(1, status: "failed", runs: [failed]) with { SourceFormat = "pdf" } };

        var cut = ctx.Render<VersionHistory>(p => p.Add(c => c.Versions, versions).Add(c => c.DocumentId, Guid.NewGuid()));

        await Assert.That(cut.Markup).Contains("The PDF is password-protected.");
    }

    [Test]
    public async Task MetadataPanel_latestPdfVersion_showsFormatSizeLinkAndWarning()
    {
        var (ctx, _, _) = NewContext();
        using var _c = ctx;
        var detail = Samples.Detail(Samples.DocId) with
        {
            LatestVersion = new LatestVersionInfo(Guid.NewGuid(), 3, "indexed", "pdf", "report.pdf", 3 * 1024 * 1024, 40, DateTimeOffset.UtcNow, [5]),
        };

        var cut = ctx.Render<DocumentMetadataPanel>(p => p.Add(c => c.Document, detail));

        await Assert.That(cut.Markup).Contains("report.pdf · PDF · 3 MiB");
        await Assert.That(cut.Find($"a.original-file-link[href='api/documents/{Samples.DocId}/versions/3/file']").TextContent.Trim()).IsEqualTo("Open original");
        await Assert.That(cut.Find(".version-warning").TextContent.Trim()).IsEqualTo("Page 5 has no extractable text and is not searchable.");
    }

    // AC-33
    [Test]
    public async Task Uploader_choosingAFileInTheDialog_doesNotTouchTheBrowserModule()
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose; // the Fluent components make their own calls; only ours must stay away
        ctx.Services.AddFluentUIComponents();
        ctx.Services.AddSingleton<IFileUploader, BrowserFileUploader>();

        var cut = RenderImport(ctx);
        Choose(cut, "report.pdf");

        await Assert.That(ctx.JSInterop.Invocations.Any(i => i.Arguments.Any(a => a is string path && path.Contains("upload.js", StringComparison.Ordinal)))).IsFalse();
    }

    [Test]
    public async Task Uploader_onFirstUpload_importsTheModuleOnceAndPassesTheRequestToIt()
    {
        var ctx = new BunitContext();
        using var _c = ctx;
        ctx.JSInterop.Mode = JSRuntimeMode.Strict;
        var module = ctx.JSInterop.SetupModule("./js/upload.js");
        module.Setup<UploadResult>("upload", _ => true).SetResult(new UploadResult(202, "{}"));
        var uploader = new BrowserFileUploader(ctx.JSInterop.JSRuntime);
        var request = new UploadRequest(
            "api/documents",
            "#up-file input[type=file]",
            [new("title", "T"), new("tags", "a"), new("tags", "b")],
            UploadHeaders.Default);

        var first = await uploader.UploadAsync(request, new Progress<UploadProgress>(), CancellationToken.None);
        await uploader.UploadAsync(request, new Progress<UploadProgress>(), CancellationToken.None);

        await Assert.That(first).IsEqualTo(new UploadResult(202, "{}"));
        await Assert.That(ctx.JSInterop.Invocations.Count(i => i.Identifier == "import")).IsEqualTo(1);
        var call = module.Invocations.First(i => i.Identifier == "upload");
        await Assert.That(call.Arguments[1]).IsEqualTo("#up-file input[type=file]");
        await Assert.That(call.Arguments[2]).IsEqualTo("api/documents");
        await Assert.That(module.Invocations.Count(i => i.Identifier == "upload")).IsEqualTo(2);
    }

    [Test]
    public async Task Uploader_cancelled_tellsTheModuleToAbortTheRequest()
    {
        var ctx = new BunitContext();
        using var _c = ctx;
        ctx.JSInterop.Mode = JSRuntimeMode.Strict;
        var module = ctx.JSInterop.SetupModule("./js/upload.js");
        var pending = module.Setup<UploadResult>("upload", _ => true);
        module.SetupVoid("abort", _ => true).SetVoidResult();
        var uploader = new BrowserFileUploader(ctx.JSInterop.JSRuntime);
        using var cts = new CancellationTokenSource();
        var request = new UploadRequest("api/documents", "#up-file input[type=file]", [], UploadHeaders.Default);

        var upload = uploader.UploadAsync(request, new Progress<UploadProgress>(), cts.Token);
        await cts.CancelAsync();
        for (var i = 0; i < 100 && !module.Invocations.Any(x => x.Identifier == "abort"); i++)
        {
            await Task.Delay(20);
        }

        pending.SetResult(new UploadResult(UploadResult.AbortedStatus, null));
        var result = await upload;

        await Assert.That(module.Invocations.Any(i => i.Identifier == "abort")).IsTrue();
        await Assert.That(result.IsAborted).IsTrue();
    }
}
