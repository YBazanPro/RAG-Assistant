using AngleSharp.Dom;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using RagAssistant.Security;
using RagAssistant.Web.Client.Documents;
using RagAssistant.Web.Client.Pages;

namespace RagAssistant.Web.Tests.Documents;

// Spec 018, AC-10 and the language choice of the import and edit forms.
public class DocumentLanguageUiTests
{
    private static readonly Guid Id = Samples.DocId;

    private static (BunitContext Ctx, FakeDocumentsApi Api) NewContext(DocumentInfo document, bool owner = true)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddFluentUIComponents();
        var api = new FakeDocumentsApi { OnGet = _ => ApiResult.Ok(document) };
        ctx.Services.AddSingleton<IDocumentsApi>(api);
        ctx.Services.AddSingleton<IFileUploader>(api.Uploader);
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

    private static IElement? Button<T>(IRenderedComponent<T> cut, string text)
        where T : IComponent =>
        cut.FindAll("fluent-button").FirstOrDefault(b => b.TextContent.Trim() == text);

    private static DocumentInfo OwnerChoseEnglish(string? detected = "fr", string? detection = "confident") =>
        Samples.Detail(Id) with { IsOwner = true, LanguageSource = "owner", DetectedLanguage = detected, LanguageDetection = detection };

    // AC-10
    [Test]
    public async Task Detail_ownerLanguageDiffersFromConfidentDetection_offersUseFrench_andSavesIt()
    {
        var document = OwnerChoseEnglish();
        var (ctx, api) = NewContext(document);
        using var _c = ctx;
        var cut = RenderPage(ctx);

        await Assert.That(cut.Markup).Contains("Detected: French");
        Button(cut, "Use French")!.Click();

        cut.WaitForState(() => api.UpdateCalls.Count == 1, TimeSpan.FromSeconds(5));
        var (_, revision, update) = api.UpdateCalls.Single();
        await Assert.That(update.Language).IsEqualTo("fr");
        await Assert.That(update.Title).IsEqualTo(document.Title);
        await Assert.That(update.OwnerName).IsEqualTo(document.OwnerName);
        await Assert.That(update.Tags.SequenceEqual(document.Tags)).IsTrue();
        await Assert.That(revision).IsEqualTo(document.Revision);
    }

    [Test]
    public async Task Detail_noMessage_whenDetectionMatchesIsUncertainOrTheViewerCannotEdit()
    {
        var (sameCtx, _) = NewContext(OwnerChoseEnglish(detected: "en"));
        using var _s = sameCtx;
        var (uncertainCtx, _) = NewContext(OwnerChoseEnglish(detection: "uncertain"));
        using var _u = uncertainCtx;
        var (seekerCtx, _) = NewContext(OwnerChoseEnglish(), owner: false);
        using var _k = seekerCtx;

        var same = RenderPage(sameCtx);
        var uncertain = RenderPage(uncertainCtx);
        var seeker = RenderPage(seekerCtx);

        await Assert.That(same.Markup).DoesNotContain("Detected:");
        await Assert.That(uncertain.Markup).DoesNotContain("Detected:");
        await Assert.That(uncertain.Markup).Contains("Language could not be detected reliably.");
        await Assert.That(seeker.Markup).DoesNotContain("Use French");
    }

    [Test]
    public async Task Detail_detectedLanguage_isLabelledAsDetected()
    {
        var (ctx, _) = NewContext(Samples.Detail(Id) with { Language = "fr", LanguageSource = "detect", DetectedLanguage = "fr", LanguageDetection = "confident" });
        using var _c = ctx;

        var cut = RenderPage(ctx);

        await Assert.That(cut.Markup).Contains("French (detected)");
    }

    [Test]
    public async Task Edit_documentLeftToDetection_startsOnDetect_andSendsDetect()
    {
        var (ctx, api) = NewContext(Samples.Detail(Id) with { IsOwner = true, LanguageSource = "detect" });
        using var _c = ctx;
        var cut = RenderPage(ctx);

        Button(cut, "Edit")!.Click();
        await Assert.That(cut.FilterSelect("Language").Instance.Value).IsEqualTo("detect");
        Button(cut, "Save")!.Click();

        cut.WaitForState(() => api.UpdateCalls.Count == 1, TimeSpan.FromSeconds(5));
        await Assert.That(api.UpdateCalls.Single().Update.Language).IsEqualTo("detect");
    }

    [Test]
    public async Task Edit_otherLanguage_sendsTheTypedCode()
    {
        var (ctx, api) = NewContext(Samples.Detail(Id) with { IsOwner = true });
        using var _c = ctx;
        var cut = RenderPage(ctx);

        Button(cut, "Edit")!.Click();
        await Assert.That(cut.FilterSelect("Language").Instance.Value).IsEqualTo("en");
        await cut.SetSelectAsync("Language", "other");
        await cut.SetTextAsync("Language code", "DE");
        Button(cut, "Save")!.Click();

        cut.WaitForState(() => api.UpdateCalls.Count == 1, TimeSpan.FromSeconds(5));
        await Assert.That(api.UpdateCalls.Single().Update.Language).IsEqualTo("de");
    }

    [Test]
    public async Task Import_frenchChosen_sendsFr()
    {
        var (ctx, api) = NewContext(Samples.Detail(Id));
        using var _c = ctx;
        var cut = ctx.Render<DocumentUpload>(p => p.Add(c => c.Open, true));
        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary(new byte[20], "politique.md"));
        await cut.SetTextAsync("Owner", "Finance");
        await cut.SetSelectAsync("Language", "fr");

        cut.FindAll("fluent-button").First(b => b.TextContent.Trim().EndsWith("Import", StringComparison.Ordinal)).Click();

        cut.WaitForState(() => api.Uploader.Requests.Count == 1, TimeSpan.FromSeconds(5));
        await Assert.That(api.Uploader.Requests.Single().Fields.Single(f => f.Key == "language").Value).IsEqualTo("fr");
    }
}
