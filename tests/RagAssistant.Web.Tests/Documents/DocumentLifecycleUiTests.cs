using AngleSharp.Dom;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using RagAssistant.Shared.Common.Security;
using RagAssistant.Web.Client.Components;
using RagAssistant.Web.Client.Documents;
using RagAssistant.Web.Client.Pages;

namespace RagAssistant.Web.Tests.Documents;

// Spec 009, AC-23 to AC-28 (lifecycle UI).
public class DocumentLifecycleUiTests
{
    private static readonly Guid Id = Samples.DocId;

    private static (BunitContext Ctx, FakeDocumentsApi Api) NewContext(bool owner)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddFluentUIComponents();
        var api = new FakeDocumentsApi();
        ctx.Services.AddSingleton<IDocumentsApi>(api);
        ctx.Services.AddSingleton<IIngestionEventSource>(new FakeEventSource());
        ctx.Services.AddSingleton<TimeProvider>(new ManualTimeProvider());
        var auth = ctx.AddAuthorization().SetAuthorized("someone");
        auth.SetPolicies(owner ? [PolicyNames.KnowledgeSeeker, PolicyNames.ContentOwner] : [PolicyNames.KnowledgeSeeker]);
        return (ctx, api);
    }

    private static IRenderedComponent<DocumentDetail> RenderPage(BunitContext ctx)
    {
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo($"http://localhost/documents/{Id}");
        var cut = ctx.Render<DocumentDetail>(p => p.Add(c => c.DocumentId, Id));
        cut.WaitForState(() => cut.Markup.Contains("Budget Exception Policy", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        return cut;
    }

    private static IReadOnlyList<IElement> Actions(IRenderedComponent<DocumentDetail> cut) =>
        [.. cut.FindAll("[aria-label='Document actions'] fluent-button")];

    private static IElement ActionButton(IRenderedComponent<DocumentDetail> cut, string text) =>
        Actions(cut).First(b => b.TextContent.Trim() == text);

    private static IElement Button<T>(IRenderedComponent<T> cut, string text)
        where T : IComponent =>
        cut.FindAll("fluent-button").First(b => b.TextContent.Trim() == text);

    private static string? Reason(IElement button) => button.ParentElement?.GetAttribute("title");

    private static bool IsDisabled(IElement button) => button.HasAttribute("disabled");

    // AC-23
    [Test]
    public async Task Actions_ownerSeesUploadReprocessAndArchive_seekerSeesNoneAndNoEdit()
    {
        var (ownerCtx, _) = NewContext(owner: true);
        using var _o = ownerCtx;
        var owner = RenderPage(ownerCtx);
        var (seekerCtx, _) = NewContext(owner: false);
        using var _s = seekerCtx;
        var seeker = RenderPage(seekerCtx);

        await Assert.That(Actions(owner).Select(b => b.TextContent.Trim()).ToArray())
            .IsEquivalentTo(["Upload new version", "Reprocess", "Archive"]);
        await Assert.That(Button(owner, "Edit")).IsNotNull();
        await Assert.That(seeker.FindAll("[aria-label='Document actions']").Count).IsEqualTo(0);
        await Assert.That(seeker.FindAll("fluent-button").Any(b => b.TextContent.Trim() == "Edit")).IsFalse();
    }

    // AC-23
    [Test]
    public async Task Actions_archivedDocument_offersRestoreAndDisablesUploadAndReprocessWithAReason()
    {
        var (ctx, api) = NewContext(owner: true);
        using var _c = ctx;
        api.OnGet = id => ApiResult.Ok(Samples.Detail(id, "archived"));
        var cut = RenderPage(ctx);

        await Assert.That(Actions(cut).Select(b => b.TextContent.Trim()).ToArray()).IsEquivalentTo(["Upload new version", "Reprocess", "Restore"]);
        await Assert.That(IsDisabled(ActionButton(cut, "Upload new version"))).IsTrue();
        await Assert.That(Reason(ActionButton(cut, "Upload new version"))).Contains("Restore the document");
        await Assert.That(IsDisabled(ActionButton(cut, "Reprocess"))).IsTrue();
        await Assert.That(IsDisabled(ActionButton(cut, "Restore"))).IsFalse();
    }

    // AC-23
    [Test]
    [Arguments("queued", true, true)]
    [Arguments("processing", true, true)]
    [Arguments("failed", false, true)]
    [Arguments("indexed", false, false)]
    public async Task Actions_followTheLatestVersionsState(string latest, bool uploadDisabled, bool reprocessDisabled)
    {
        var (ctx, api) = NewContext(owner: true);
        using var _c = ctx;
        api.OnGet = id => ApiResult.Ok(Samples.Detail(id, latestStatus: latest));
        var cut = RenderPage(ctx);

        await Assert.That(IsDisabled(ActionButton(cut, "Upload new version"))).IsEqualTo(uploadDisabled);
        await Assert.That(IsDisabled(ActionButton(cut, "Reprocess"))).IsEqualTo(reprocessDisabled);
        if (reprocessDisabled)
        {
            await Assert.That(Reason(ActionButton(cut, "Reprocess"))).IsNotNull();
        }
    }

    // AC-24
    [Test]
    public async Task Edit_showsTheFormAndSaveSendsTheRevisionThenShowsTheSavedValues()
    {
        var (ctx, api) = NewContext(owner: true);
        using var _c = ctx;
        var cut = RenderPage(ctx);

        Button(cut, "Edit").Click();
        await Assert.That(cut.TextValue("Title")).IsEqualTo("Budget Exception Policy");
        await cut.SetTextAsync("Title", "  Renamed Policy ");
        await cut.SetTextAsync("Tags (comma-separated)", "a, b");
        Button(cut, "Save").Click();

        cut.WaitForState(() => api.UpdateCalls.Count == 1 && cut.FindAll("h1").Any(h => h.TextContent == "Renamed Policy"), TimeSpan.FromSeconds(5));
        var call = api.UpdateCalls.Single();
        await Assert.That(call.Id).IsEqualTo(Id);
        await Assert.That(call.Revision).IsEqualTo(1);
        await Assert.That(call.Update.Title).IsEqualTo("Renamed Policy");
        await Assert.That(call.Update.Tags.SequenceEqual(["a", "b"])).IsTrue();
        await Assert.That(cut.FindAll("fluent-text-input").Count).IsEqualTo(0);
        await Assert.That(cut.Markup).Contains("Changes saved.");
    }

    // AC-24
    [Test]
    public async Task Edit_invalidFieldsShowErrorsUnderTheInputsAndSendNothing()
    {
        var (ctx, api) = NewContext(owner: true);
        using var _c = ctx;
        var cut = RenderPage(ctx);

        Button(cut, "Edit").Click();
        await cut.SetTextAsync("Title", " ");
        await cut.SetTextAsync("Language", "english");
        Button(cut, "Save").Click();

        await Assert.That(cut.Markup).Contains("Title is required.");
        await Assert.That(cut.Markup).Contains("Language must be two letters");
        await Assert.That(api.UpdateCalls.Count).IsEqualTo(0);
    }

    // AC-24
    [Test]
    public async Task Edit_cancelDiscardsTheChangesAndSendsNothing()
    {
        var (ctx, api) = NewContext(owner: true);
        using var _c = ctx;
        var cut = RenderPage(ctx);

        Button(cut, "Edit").Click();
        await cut.SetTextAsync("Title", "Changed");
        Button(cut, "Cancel").Click();

        await Assert.That(cut.Find(".meta-card").TextContent).Contains("Budget Exception Policy");
        await Assert.That(cut.Find(".meta-card").TextContent).DoesNotContain("Changed");
        await Assert.That(api.UpdateCalls.Count).IsEqualTo(0);
    }

    // AC-24
    [Test]
    public async Task Edit_serverFieldErrorsAppearUnderTheirInputs()
    {
        var (ctx, api) = NewContext(owner: true);
        using var _c = ctx;
        api.OnUpdate = (_, _, _) => ApiResult.Fail<DocumentInfo>(new ApiError(
            400, "validation.failed", "invalid", new Dictionary<string, string[]> { ["ownerName"] = ["Owner name is required."] }));
        var cut = RenderPage(ctx);

        Button(cut, "Edit").Click();
        Button(cut, "Save").Click();

        cut.WaitForState(() => cut.Markup.Contains("Owner name is required.", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        await Assert.That(api.UpdateCalls.Count).IsEqualTo(1);
    }

    // AC-25
    [Test]
    public async Task Edit_conflictShowsTheBanner_reloadShowsServerValues_keepEditingPreservesInput()
    {
        var (ctx, api) = NewContext(owner: true);
        using var _c = ctx;
        api.OnUpdate = (_, _, _) => ApiResult.Fail<DocumentInfo>(new ApiError(412, "documents.edit_conflict", "stale", new Dictionary<string, string[]>()));
        var cut = RenderPage(ctx);

        Button(cut, "Edit").Click();
        await cut.SetTextAsync("Title", "My edit");
        Button(cut, "Save").Click();
        cut.WaitForState(() => cut.Markup.Contains("changed by someone else", StringComparison.Ordinal), TimeSpan.FromSeconds(5));

        Button(cut, "Keep editing").Click();
        await Assert.That(cut.TextValue("Title")).IsEqualTo("My edit");
        await Assert.That(cut.Markup).DoesNotContain("changed by someone else");

        Button(cut, "Save").Click();
        cut.WaitForState(() => cut.Markup.Contains("changed by someone else", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        api.OnGet = id => ApiResult.Ok(Samples.Detail(id, revision: 2) with { Title = "Server title" });
        Button(cut, "Reload").Click();

        cut.WaitForState(() => cut.FindAll("h1").Any(h => h.TextContent == "Server title"), TimeSpan.FromSeconds(5));
        await Assert.That(cut.FindAll("fluent-text-input").Count).IsEqualTo(0);
        Button(cut, "Edit").Click();
        Button(cut, "Save").Click();
        cut.WaitForState(() => api.UpdateCalls.Count == 3, TimeSpan.FromSeconds(5));
        await Assert.That(api.UpdateCalls[2].Revision).IsEqualTo(2);
    }

    // AC-27
    [Test]
    public async Task Archive_requiresConfirmation_cancelSendsNothing_confirmArchivesAndRefreshes()
    {
        var (ctx, api) = NewContext(owner: true);
        using var _c = ctx;
        var archived = false;
        api.OnGet = id => ApiResult.Ok(Samples.Detail(id, archived ? "archived" : "active"));
        api.OnArchive = id => { archived = true; return ApiResult.Ok(Samples.Detail(id, "archived")); };
        var cut = RenderPage(ctx);
        var dialog = cut.FindComponent<ConfirmDialog>();

        ActionButton(cut, "Archive").Click();
        await Assert.That(dialog.Instance.Open).IsTrue();
        await Assert.That(dialog.Instance.Destructive).IsTrue();
        await cut.InvokeAsync(() => dialog.Instance.OnCancel.InvokeAsync());
        await Assert.That(api.ArchiveCalls.Count).IsEqualTo(0);
        await Assert.That(dialog.Instance.Open).IsFalse();

        ActionButton(cut, "Archive").Click();
        await cut.InvokeAsync(() => dialog.Instance.OnConfirm.InvokeAsync());

        cut.WaitForState(() => Actions(cut).Any(b => b.TextContent.Trim() == "Restore"), TimeSpan.FromSeconds(5));
        await Assert.That(api.ArchiveCalls.Single()).IsEqualTo(Id);
        await Assert.That(cut.Find(".status-badge").TextContent).Contains("Archived");
    }

    // AC-27
    [Test]
    public async Task Reprocess_requiresConfirmationThenCallsTheApiAndReloads()
    {
        var (ctx, api) = NewContext(owner: true);
        using var _c = ctx;
        var reprocessed = false;
        api.OnGet = id => ApiResult.Ok(reprocessed
            ? Samples.Detail(id, run: Samples.Run("queued", "queued"), latestStatus: "queued")
            : Samples.Detail(id));
        api.OnReprocess = id => { reprocessed = true; return ApiResult.Ok(new VersionAccepted(id, Guid.NewGuid(), 2, Guid.NewGuid(), "queued")); };
        var cut = RenderPage(ctx);
        var dialog = cut.FindComponent<ConfirmDialog>();

        ActionButton(cut, "Reprocess").Click();
        await Assert.That(dialog.Instance.Open).IsTrue();
        await Assert.That(dialog.Instance.Message).Contains("new version from the stored content");
        await Assert.That(api.ReprocessCalls.Count).IsEqualTo(0);
        await cut.InvokeAsync(() => dialog.Instance.OnConfirm.InvokeAsync());

        cut.WaitForState(() => IsDisabled(ActionButton(cut, "Reprocess")), TimeSpan.FromSeconds(5));
        await Assert.That(api.ReprocessCalls.Single()).IsEqualTo(Id);
    }

    // AC-27
    [Test]
    public async Task Restore_duplicateConflictShowsAMessageWithALink()
    {
        var (ctx, api) = NewContext(owner: true);
        using var _c = ctx;
        var existing = Guid.NewGuid();
        api.OnGet = id => ApiResult.Ok(Samples.Detail(id, "archived"));
        api.OnRestore = _ => ApiResult.Fail<DocumentInfo>(new ApiError(
            409, "documents.duplicate_content", "dup", new Dictionary<string, string[]>(), existing));
        var cut = RenderPage(ctx);
        var dialog = cut.FindComponent<ConfirmDialog>();

        ActionButton(cut, "Restore").Click();
        await cut.InvokeAsync(() => dialog.Instance.OnConfirm.InvokeAsync());

        cut.WaitForState(() => cut.Markup.Contains("Another active document already has this content", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        await Assert.That(cut.Markup).Contains($"documents/{existing}");
    }

    // AC-26
    [Test]
    public async Task UploadNewVersion_opensTheDialogInNewVersionModeWithOnlyTheFilePicker()
    {
        var (ctx, _) = NewContext(owner: true);
        using var _c = ctx;
        var cut = RenderPage(ctx);

        ActionButton(cut, "Upload new version").Click();
        var upload = cut.FindComponent<DocumentUpload>();

        await Assert.That(upload.Instance.Open).IsTrue();
        await Assert.That(upload.Instance.DocumentId).IsEqualTo(Id);
        await Assert.That(upload.FindComponents<Microsoft.FluentUI.AspNetCore.Components.FluentTextInput>().Count).IsEqualTo(0);
        await Assert.That(upload.Markup).Contains("Budget Exception Policy");
    }

    private static IRenderedComponent<DocumentUpload> RenderVersionDialog(
        BunitContext ctx, Action<VersionAccepted>? uploaded = null, Action? stale = null) =>
        ctx.Render<DocumentUpload>(p => p
            .Add(c => c.Open, true)
            .Add(c => c.DocumentId, Id)
            .Add(c => c.DocumentTitle, "Budget Exception Policy")
            .Add(c => c.OnVersionUploaded, EventCallback.Factory.Create<VersionAccepted>(ctx, a => uploaded?.Invoke(a)))
            .Add(c => c.OnStale, EventCallback.Factory.Create(ctx, () => stale?.Invoke())));

    private static void Choose(IRenderedComponent<DocumentUpload> cut, string name, int size = 20) =>
        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary(new byte[size], name));

    // AC-26
    [Test]
    public async Task VersionDialog_validFile_uploadsAndReportsTheAcceptedVersion()
    {
        var (ctx, api) = NewContext(owner: true);
        using var _c = ctx;
        VersionAccepted? accepted = null;
        var cut = RenderVersionDialog(ctx, a => accepted = a);

        Choose(cut, "policy-v2.md");
        Button(cut, "Upload").Click();

        cut.WaitForState(() => accepted is not null, TimeSpan.FromSeconds(5));
        await Assert.That(api.UploadVersionCalls.Single()).IsEqualTo((Id, "policy-v2.md"));
        await Assert.That(accepted!.VersionNumber).IsEqualTo(2);
    }

    // AC-26
    [Test]
    [Arguments("report.pdf", 20, "Only .md, .markdown and .txt")]
    [Arguments("big.md", 1_048_577, "File too large")]
    public async Task VersionDialog_invalidFile_showsAClientErrorAndSendsNothing(string name, int size, string message)
    {
        var (ctx, api) = NewContext(owner: true);
        using var _c = ctx;
        var cut = RenderVersionDialog(ctx);

        Choose(cut, name, size);
        Button(cut, "Upload").Click();

        await Assert.That(cut.Find("#up-file-error").TextContent).Contains(message);
        await Assert.That(api.UploadVersionCalls.Count).IsEqualTo(0);
    }

    // AC-26
    [Test]
    public async Task VersionDialog_duplicateContentLinksToTheExistingDocument()
    {
        var (ctx, api) = NewContext(owner: true);
        using var _c = ctx;
        var existing = Guid.NewGuid();
        api.OnUploadVersion = _ => ApiResult.Fail<VersionAccepted>(new ApiError(
            409, "documents.duplicate_content", "dup", new Dictionary<string, string[]>(), existing));
        var cut = RenderVersionDialog(ctx);

        Choose(cut, "same.md");
        Button(cut, "Upload").Click();

        cut.WaitForState(() => cut.Markup.Contains($"documents/{existing}", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        await Assert.That(cut.Markup).Contains("already in the corpus");
    }

    // AC-26
    [Test]
    [Arguments("documents.archived", "archived")]
    [Arguments("documents.ingestion_in_progress", "still being indexed")]
    public async Task VersionDialog_stateConflictsShowAMessageAndAskThePageToRefresh(string code, string expected)
    {
        var (ctx, api) = NewContext(owner: true);
        using var _c = ctx;
        api.OnUploadVersion = _ => ApiResult.Fail<VersionAccepted>(new ApiError(409, code, "conflict", new Dictionary<string, string[]>()));
        var staleCalls = 0;
        var cut = RenderVersionDialog(ctx, stale: () => staleCalls++);

        Choose(cut, "next.md");
        Button(cut, "Upload").Click();

        cut.WaitForState(() => cut.Markup.Contains(expected, StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        await Assert.That(staleCalls).IsEqualTo(1);
    }

    // AC-26
    [Test]
    public async Task UploadFlowOnThePage_closesTheDialogAndShowsTheNewRun()
    {
        var (ctx, api) = NewContext(owner: true);
        using var _c = ctx;
        var uploaded = false;
        api.OnGet = id => ApiResult.Ok(uploaded
            ? Samples.Detail(id, run: Samples.Run("queued", "queued", attempt: 1), latestStatus: "queued")
            : Samples.Detail(id));
        api.OnUploadVersion = id => { uploaded = true; return ApiResult.Ok(new VersionAccepted(id, Guid.NewGuid(), 2, Guid.NewGuid(), "queued")); };
        var cut = RenderPage(ctx);

        ActionButton(cut, "Upload new version").Click();
        var upload = cut.FindComponent<DocumentUpload>();
        upload.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary(new byte[20], "v2.md"));
        Button(upload, "Upload").Click();

        cut.WaitForState(() => IsDisabled(ActionButton(cut, "Upload new version")), TimeSpan.FromSeconds(5));
        await Assert.That(cut.FindComponent<DocumentUpload>().Instance.Open).IsFalse();
        await Assert.That(api.UploadVersionCalls.Count).IsEqualTo(1);
    }

    // AC-28
    [Test]
    public async Task History_showsTheOriginOfEachVersion()
    {
        var (ctx, _) = NewContext(owner: true);
        using var _c = ctx;
        var cut = ctx.Render<VersionHistory>(p => p.Add(c => c.Versions, new[]
        {
            Samples.Version(2, isActive: true) with { Origin = "reprocess" },
            Samples.Version(1),
        }));

        await Assert.That(cut.Markup).Contains("Version 2");
        await Assert.That(cut.Markup).Contains("Reprocess");
        await Assert.That(cut.Markup).Contains("Upload");
    }
}
