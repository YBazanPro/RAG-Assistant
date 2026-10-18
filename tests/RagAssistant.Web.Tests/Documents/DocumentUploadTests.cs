using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using RagAssistant.Web.Client.Documents;

namespace RagAssistant.Web.Tests.Documents;

// Spec 008, AC-21 to AC-24 (import dialog).
public class DocumentUploadTests
{
    private static (BunitContext Ctx, FakeDocumentsApi Api) NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddFluentUIComponents();
        var api = new FakeDocumentsApi();
        ctx.Services.AddSingleton<IDocumentsApi>(api);
        ctx.Services.AddSingleton<IFileUploader>(api.Uploader);
        return (ctx, api);
    }

    private static IRenderedComponent<DocumentUpload> Render(BunitContext ctx, Action<ImportAccepted>? imported = null, Action? closed = null) =>
        ctx.Render<DocumentUpload>(p => p
            .Add(c => c.Open, true)
            .Add(c => c.OnImported, EventCallback.Factory.Create<ImportAccepted>(ctx, a => imported?.Invoke(a)))
            .Add(c => c.OnClose, EventCallback.Factory.Create(ctx, () => closed?.Invoke())));

    private static void Choose(IRenderedComponent<DocumentUpload> cut, string name, int size = 20) =>
        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary(new byte[size], name));

    private static void Submit(IRenderedComponent<DocumentUpload> cut) =>
        cut.FindAll("fluent-button").First(b => b.TextContent.Trim().EndsWith("Import", StringComparison.Ordinal)).Click();

    [Test]
    public async Task Choosing_reportMd_defaultsTitleToTheFileNameAndLanguageToDetect()
    {
        var (ctx, _) = NewContext();
        using var _c = ctx;
        var cut = Render(ctx);

        Choose(cut, "report.md");

        await Assert.That(cut.TextValue("Title")).IsEqualTo("report");
        // Spec 018: the language is detected at ingestion unless the owner picks one.
        await Assert.That(cut.FilterSelect("Language").Instance.Value).IsEqualTo("detect");
    }

    [Test]
    [Arguments("report.docx", 20, "Only .md, .markdown, .txt and .pdf")]
    [Arguments("big.md", 1_048_577, "File too large (the limit is 1 MiB)")]
    [Arguments("big.pdf", 104_857_601, "File too large (the limit is 100 MiB)")]
    public async Task Submit_invalidFile_showsAClientErrorAndSendsNothing(string name, int size, string message)
    {
        var (ctx, api) = NewContext();
        using var _c = ctx;
        var cut = Render(ctx);
        Choose(cut, name, size);
        await cut.SetTextAsync("Owner", "Finance");

        Submit(cut);

        await Assert.That(cut.Find("#up-file-error").TextContent).Contains(message);
        await Assert.That(api.Uploader.Requests.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Submit_missingRequiredFields_showFieldErrorsAndSendNothing()
    {
        var (ctx, api) = NewContext();
        using var _c = ctx;
        var cut = Render(ctx);
        Choose(cut, "report.md");

        Submit(cut);

        await Assert.That(cut.Markup).Contains("Owner is required.");
        await Assert.That(api.Uploader.Requests.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Submit_validInput_sendsTheRequestAndReportsTheAcceptedImport()
    {
        var (ctx, api) = NewContext();
        using var _c = ctx;
        ImportAccepted? accepted = null;
        var cut = Render(ctx, a => accepted = a);
        Choose(cut, "report.md");
        await cut.SetTextAsync("Owner", "Finance");
        await cut.SetTextAsync("Tags (comma-separated)", "budget, finance");

        Submit(cut);

        cut.WaitForState(() => accepted is not null, TimeSpan.FromSeconds(5));
        var request = api.Uploader.Requests.Single();
        await Assert.That(request.Url).IsEqualTo("api/documents");
        await Assert.That(request.FileInputSelector).IsEqualTo(DocumentUpload.FileInputSelector);
        await Assert.That(request.Headers["X-Requested-With"]).IsEqualTo("ragassistant");
        await Assert.That(request.Fields.Where(f => f.Key == "title").Select(f => f.Value).ToArray()).IsEquivalentTo(["report"]);
        await Assert.That(request.Fields.Where(f => f.Key == "ownerName").Select(f => f.Value).ToArray()).IsEquivalentTo(["Finance"]);
        await Assert.That(request.Fields.Where(f => f.Key == "tags").Select(f => f.Value).ToArray()).IsEquivalentTo(["budget", "finance"]);
        await Assert.That(request.Fields.Any(f => f.Key == "file")).IsFalse();
    }

    [Test]
    public async Task Submit_whileUploading_disablesControlsAndShowsASpinner()
    {
        var (ctx, api) = NewContext();
        using var _c = ctx;
        var gate = new TaskCompletionSource();
        var cut = Render(ctx);
        Choose(cut, "report.md");
        await cut.SetTextAsync("Owner", "Finance");
        api.Uploader.Gate = gate.Task;

        Submit(cut);
        cut.WaitForState(() => cut.Markup.Contains("Uploading", StringComparison.Ordinal), TimeSpan.FromSeconds(5));

        await Assert.That(cut.TextInput("Title").Instance.Disabled).IsTrue();
        gate.SetResult();
    }

    [Test]
    [Arguments(400, "validation.failed", "title", "Title is bad", "Title is bad")]
    [Arguments(400, "documents.unsupported_extension", null, "Unsupported type", "Unsupported type")]
    [Arguments(403, "auth.forbidden", null, "no", "You are not allowed to import documents.")]
    [Arguments(401, "auth.unauthenticated", null, "no", "session has expired")]
    [Arguments(413, "http.413", null, "big", "File too large")]
    [Arguments(0, "network", null, "down", "check your connection")]
    [Arguments(500, "http.500", null, "oops", "check your connection")]
    public async Task Submit_serverError_isShownWhereSpecifiedAndKeepsTheFormValues(int status, string code, string? field, string message, string expected)
    {
        var (ctx, api) = NewContext();
        using var _c = ctx;
        var fields = field is null ? new Dictionary<string, string[]>() : new Dictionary<string, string[]> { [field] = [message] };
        api.Uploader.OnUpload = _ => FakeFileUploader.Failure(new ApiError(status, code, message, fields));
        var cut = Render(ctx);
        Choose(cut, "report.md");
        await cut.SetTextAsync("Owner", "Finance");

        Submit(cut);

        cut.WaitForState(() => cut.Markup.Contains(expected, StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(5));
        await Assert.That(cut.TextValue("Owner")).IsEqualTo("Finance");
        await Assert.That(cut.TextValue("Title")).IsEqualTo("report");
        if (field == "title")
        {
            await Assert.That(cut.TextInput("Title").Instance.Message).Contains(expected);
        }

        if (code == "documents.unsupported_extension" || status == 413)
        {
            await Assert.That(cut.Find("#up-file-error").TextContent).Contains(expected);
        }
    }

    [Test]
    public async Task Submit_duplicateContent_linksToTheExistingDocument()
    {
        var (ctx, api) = NewContext();
        using var _c = ctx;
        var existing = Guid.NewGuid();
        api.Uploader.OnUpload = _ => FakeFileUploader.Failure(
            new ApiError(409, "documents.duplicate_content", "dup", new Dictionary<string, string[]>(), existing));
        var cut = Render(ctx);
        Choose(cut, "report.md");
        await cut.SetTextAsync("Owner", "Finance");

        Submit(cut);

        cut.WaitForState(() => cut.Markup.Contains("already in the corpus", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        await Assert.That(cut.Find("fluent-link").GetAttribute("href")).IsEqualTo($"documents/{existing}");
    }

    [Test]
    public async Task Escape_requestsClose()
    {
        var (ctx, _) = NewContext();
        using var _c = ctx;
        var closed = 0;
        var cut = Render(ctx, closed: () => closed++);

        cut.Find("div").KeyDown(new KeyboardEventArgs { Key = "Escape" });

        await Assert.That(closed).IsEqualTo(1);
    }
}
