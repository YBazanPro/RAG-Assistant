using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using RagAssistant.Web.Client.Documents;

namespace RagAssistant.Web.Tests.Documents;

// Spec 016, AC-25 to AC-27 (import form, list badge, metadata panel).
public class VisibilityUiTests
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

    private static IElement Button<T>(IRenderedComponent<T> cut, string text)
        where T : IComponent =>
        cut.FindAll("fluent-button").First(b => b.TextContent.Trim() == text);

    private static Task ChooseAsync<T>(IRenderedComponent<T> cut, string value)
        where T : class, IComponent
    {
        var group = cut.FindComponent<FluentRadioGroup<string>>();
        return cut.InvokeAsync(() => group.Instance.ValueChanged.InvokeAsync(value));
    }

    private static async Task<IRenderedComponent<DocumentUpload>> RenderAndSubmitAsync(BunitContext ctx, FakeDocumentsApi api, string? visibility)
    {
        var cut = ctx.Render<DocumentUpload>(p => p.Add(c => c.Open, true));
        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary(new byte[20], "report.md"));
        await cut.SetTextAsync("Owner", "Finance");
        if (visibility is not null)
        {
            await ChooseAsync(cut, visibility);
        }

        cut.FindAll("fluent-button").First(b => b.TextContent.Trim().EndsWith("Import", StringComparison.Ordinal)).Click();
        cut.WaitForState(() => api.Uploader.Requests.Count == 1, TimeSpan.FromSeconds(5));
        return cut;
    }

    // AC-25
    [Test]
    public async Task Import_defaultsToShared()
    {
        var (ctx, api) = NewContext();
        using var _c = ctx;

        await RenderAndSubmitAsync(ctx, api, visibility: null);

        await Assert.That(api.Uploader.Requests.Single().Fields.Single(f => f.Key == "visibility").Value).IsEqualTo("shared");
    }

    // AC-25
    [Test]
    public async Task Import_choosingPrivate_sendsPrivate()
    {
        var (ctx, api) = NewContext();
        using var _c = ctx;

        await RenderAndSubmitAsync(ctx, api, "private");

        await Assert.That(api.Uploader.Requests.Single().Fields.Single(f => f.Key == "visibility").Value).IsEqualTo("private");
    }

    // AC-26
    [Test]
    public async Task Table_marksPrivateDocumentsWithTextAndLeavesSharedOnesUnmarked()
    {
        var (ctx, _) = NewContext();
        using var _c = ctx;
        var rows = new[]
        {
            Samples.Summary("Mine") with { Visibility = "private", IsOwner = true },
            Samples.Summary("Everyone's"),
        };

        var cut = ctx.Render<DocumentTable>(p => p
            .Add(c => c.Result, new PagedResult<DocumentSummary>(rows, 1, 25, 2))
            .Add(c => c.Sort, "-updated"));

        var badges = cut.FindAll(".visibility-badge");
        await Assert.That(badges.Count).IsEqualTo(1);
        await Assert.That(badges[0].TextContent).Contains("Private");
    }

    // AC-27
    [Test]
    public async Task MetadataPanel_ownerSwitchingToPrivate_asksFirst_thenSendsTheVisibility()
    {
        var (ctx, api) = NewContext();
        using var _c = ctx;
        var document = Samples.Detail(Guid.NewGuid()) with { IsOwner = true, CreatedBy = "owner-sub" };
        var cut = ctx.Render<DocumentMetadataPanel>(p => p.Add(c => c.Document, document).Add(c => c.CanEdit, true));

        Button(cut, "Edit").Click();
        await ChooseAsync(cut, "private");
        Button(cut, "Save").Click();
        await Assert.That(api.UpdateCalls.Count).IsEqualTo(0);
        await Assert.That(cut.Markup).Contains("no longer find this document");

        Button(cut, "Make private").Click();

        cut.WaitForState(() => api.UpdateCalls.Count == 1, TimeSpan.FromSeconds(5));
        await Assert.That(api.UpdateCalls.Single().Update.Visibility).IsEqualTo("private");
    }

    // AC-27
    [Test]
    public async Task MetadataPanel_anotherContentOwner_seesVisibilityReadOnly_andSendsNone()
    {
        var (ctx, api) = NewContext();
        using var _c = ctx;
        var cut = ctx.Render<DocumentMetadataPanel>(p => p.Add(c => c.Document, Samples.Detail(Guid.NewGuid())).Add(c => c.CanEdit, true));

        Button(cut, "Edit").Click();
        Button(cut, "Save").Click();

        await Assert.That(cut.FindComponents<FluentRadioGroup<string>>().Count).IsEqualTo(0);
        cut.WaitForState(() => api.UpdateCalls.Count == 1, TimeSpan.FromSeconds(5));
        await Assert.That(api.UpdateCalls.Single().Update.Visibility).IsNull();
    }

    [Test]
    public async Task MetadataPanel_hidesTheImporterFromPeopleWhoAreNotTheOwner()
    {
        var (ctx, _) = NewContext();
        using var _c = ctx;
        var document = Samples.Detail(Guid.NewGuid()) with { CreatedBy = null, UpdatedBy = null };

        var cut = ctx.Render<DocumentMetadataPanel>(p => p.Add(c => c.Document, document));

        await Assert.That(cut.Markup).DoesNotContain(" by ");
    }
}
