using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using RagAssistant.Shared.Common.Security;
using RagAssistant.Web.Client.Documents;
using DocumentsPage = RagAssistant.Web.Client.Pages.Documents;

namespace RagAssistant.Web.Tests.Documents;

// Spec 008, AC-15 to AC-18 and AC-20 (list page).
public class DocumentsPageTests
{
    private static (BunitContext Ctx, FakeDocumentsApi Api, ManualTimeProvider Clock) NewContext(bool owner = false)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddFluentUIComponents();
        var api = new FakeDocumentsApi();
        var clock = new ManualTimeProvider();
        ctx.Services.AddSingleton<IDocumentsApi>(api);
        ctx.Services.AddSingleton<IFileUploader>(api.Uploader);
        ctx.Services.AddSingleton<TimeProvider>(clock);
        var auth = ctx.AddAuthorization().SetAuthorized("someone");
        auth.SetPolicies(owner ? [PolicyNames.KnowledgeSeeker, PolicyNames.ContentOwner] : [PolicyNames.KnowledgeSeeker]);
        return (ctx, api, clock);
    }

    private static IRenderedComponent<DocumentsPage> Render(BunitContext ctx, string url = "http://localhost/documents")
    {
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo(url);
        return ctx.Render<DocumentsPage>();
    }

    [Test]
    public async Task Page_rendersRowsWithBadgeLanguageOwnerTagsLocalTimeAndDashForNullPassageCount()
    {
        var (ctx, api, _) = NewContext();
        using var _c = ctx;
        api.OnList = _ => ApiResult.Ok(Samples.Page(Samples.Summary("Budget Policy"), Samples.Summary("Draft", status: "queued", passages: null)));

        var cut = Render(ctx);
        cut.WaitForState(() => (cut.FindAll("tbody tr").Count) == 2, TimeSpan.FromSeconds(5));

        var rows = cut.FindAll("tbody tr");
        await Assert.That(rows[0].QuerySelector("fluent-link")!.TextContent).IsEqualTo("Budget Policy");
        await Assert.That(rows[0].QuerySelector(".status-badge")!.TextContent).Contains("Active");
        await Assert.That(rows[0].QuerySelector(".status-icon")!.GetAttribute("aria-hidden")).IsEqualTo("true");
        await Assert.That(rows[0].TextContent).Contains("Finance");
        await Assert.That(rows[0].TextContent).Contains("budget");
        await Assert.That(rows[0].QuerySelector("time")!.GetAttribute("title")).IsEqualTo("2026-10-01 09:30 UTC");
        await Assert.That(rows[1].QuerySelector(".status-badge")!.TextContent).Contains("Queued");
        var cells = rows[1].QuerySelectorAll("td");
        await Assert.That(cells[cells.Length - 1].TextContent).IsEqualTo("—");
        await Assert.That(cut.Find("table").GetAttribute("aria-label")).Contains("results");
        var sortable = cut.FindAll("th").Where(h => h.QuerySelector("[col-sort-button]") is not null).ToList();
        await Assert.That(string.Join(",", sortable.Select(h => h.GetAttribute("aria-sort") + ":" + h.TextContent.Trim())))
            .IsEqualTo("none:Title,none:Effective,descending:Updated");
    }

    [Test]
    public async Task Page_whileLoading_showsLoadingState_andLoadsOnceAfterFirstRender()
    {
        var (ctx, api, _) = NewContext();
        using var _c = ctx;
        var gate = new TaskCompletionSource();
        api.ListGate = gate.Task;

        var cut = Render(ctx);

        await Assert.That(cut.Markup).Contains("Loading documents");
        gate.SetResult();
        cut.WaitForState(() => (cut.FindAll("tbody tr").Count) == 1, TimeSpan.FromSeconds(5));
        await Assert.That(api.ListCalls.Count).IsEqualTo(1);
        await Assert.That(api.FacetsCalls).IsEqualTo(1);
    }

    [Test]
    public async Task Page_emptyCorpus_showsEmptyState_withImportOnlyForOwners()
    {
        var (seekerCtx, seekerApi, _) = NewContext(owner: false);
        using var _s = seekerCtx;
        seekerApi.OnList = _ => ApiResult.Ok(Samples.Empty());
        var seeker = Render(seekerCtx);
        seeker.WaitForState(() => (seeker.Markup).Contains("No documents yet"), TimeSpan.FromSeconds(5));

        var (ownerCtx, ownerApi, _) = NewContext(owner: true);
        using var _o = ownerCtx;
        ownerApi.OnList = _ => ApiResult.Ok(Samples.Empty());
        var owner = Render(ownerCtx);
        owner.WaitForState(() => (owner.Markup).Contains("No documents yet"), TimeSpan.FromSeconds(5));

        await Assert.That(seeker.Markup).DoesNotContain("Import");
        await Assert.That(owner.Markup).Contains("Import");
    }

    [Test]
    public async Task Page_filtersMatchNothing_showsFilteredEmptyStateWithClearFilters()
    {
        var (ctx, api, _) = NewContext();
        using var _c = ctx;
        api.OnList = _ => ApiResult.Ok(Samples.Empty());

        var cut = Render(ctx, "http://localhost/documents?status=failed");
        cut.WaitForState(() => (cut.Markup).Contains("No document matches"), TimeSpan.FromSeconds(5));

        cut.FindAll("fluent-button").First(b => b.TextContent.Contains("Clear filters")).Click();

        await Assert.That(ctx.Services.GetRequiredService<NavigationManager>().Uri).IsEqualTo("http://localhost/documents");
    }

    [Test]
    public async Task Page_apiFails_showsErrorStateWithWorkingRetry()
    {
        var (ctx, api, _) = NewContext();
        using var _c = ctx;
        var fail = true;
        api.OnList = _ => fail
            ? ApiResult.Fail<PagedResult<DocumentSummary>>(new ApiError(502, "upstream.unavailable", "down", new Dictionary<string, string[]>()))
            : ApiResult.Ok(Samples.Page());

        var cut = Render(ctx);
        cut.WaitForState(() => (cut.Markup).Contains("Documents could not be loaded"), TimeSpan.FromSeconds(5));
        fail = false;
        cut.FindAll("fluent-button").First(b => b.TextContent.Contains("Try again")).Click();

        cut.WaitForState(() => (cut.FindAll("tbody tr").Count) == 1, TimeSpan.FromSeconds(5));
        await Assert.That(api.ListCalls.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Page_urlState_isSentToTheApiAndReflectedInTheControls()
    {
        var (ctx, api, _) = NewContext();
        using var _c = ctx;

        var cut = Render(ctx, "http://localhost/documents?q=a&status=failed&tags=x&tags=y&sort=title&page=2&bogus=1");
        cut.WaitForState(() => (api.ListCalls.Count) == 1, TimeSpan.FromSeconds(5));

        await Assert.That(api.ListCalls[0].ToQueryString()).IsEqualTo("q=a&status=failed&tags=x&tags=y&sort=title&page=2");
        await Assert.That(cut.TextValue("Search titles")).IsEqualTo("a");
        await Assert.That(cut.FilterSelect("Status").Instance.Value).IsEqualTo("failed");
    }

    [Test]
    public async Task Page_filterChange_updatesTheUrlAndResetsThePage()
    {
        var (ctx, _, _) = NewContext();
        using var _c = ctx;
        var cut = Render(ctx, "http://localhost/documents?page=3");
        cut.WaitForState(() => cut.FindComponents<Microsoft.FluentUI.AspNetCore.Components.FluentTextInput>().Count > 0, TimeSpan.FromSeconds(5));

        await cut.SetSelectAsync("Status", "failed");

        await Assert.That(ctx.Services.GetRequiredService<NavigationManager>().Uri).IsEqualTo("http://localhost/documents?status=failed");
    }

    [Test]
    public async Task Page_invalidUrlValues_areDropped()
    {
        var (ctx, api, _) = NewContext();
        using var _c = ctx;

        var cut = Render(ctx, "http://localhost/documents?status=foo&page=0&effectiveFrom=nope");
        cut.WaitForState(() => (api.ListCalls.Count) == 1, TimeSpan.FromSeconds(5));

        await Assert.That(api.ListCalls[0].ToQueryString()).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Page_searchTyping_isDebouncedAndReplacesTheHistoryEntry()
    {
        var (ctx, _, clock) = NewContext();
        using var _c = ctx;
        var cut = Render(ctx);
        cut.WaitForState(() => cut.FindComponents<Microsoft.FluentUI.AspNetCore.Components.FluentTextInput>().Count > 0, TimeSpan.FromSeconds(5));
        var nav = ctx.Services.GetRequiredService<NavigationManager>();

        // The handler awaits the debounce delay, so the call completes only once the clock has moved: do not await it yet.
        var typing = cut.SetTextAsync("Search titles", "bud");
        for (var i = 0; i < 250 && clock.ActiveTimers == 0; i++)
        {
            await Task.Delay(20);
        }

        clock.Advance(TimeSpan.FromMilliseconds(299));
        await Assert.That(nav.Uri).IsEqualTo("http://localhost/documents");
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await typing;

        await Assert.That(nav.Uri).IsEqualTo("http://localhost/documents?q=bud");
    }

    [Test]
    public async Task Page_busyRows_pollEvery5sUntilNoneIsBusy_andStopOnDispose()
    {
        var (ctx, api, clock) = NewContext();
        using var _c = ctx;
        var busy = true;
        api.OnList = _ => ApiResult.Ok(Samples.Page(Samples.Summary(status: busy ? "processing" : "active")));

        var cut = Render(ctx);
        cut.WaitForState(() => (api.ListCalls.Count) == 1, TimeSpan.FromSeconds(5));

        clock.Advance(TimeSpan.FromSeconds(4.9));
        await Assert.That(api.ListCalls.Count).IsEqualTo(1);
        clock.Advance(TimeSpan.FromSeconds(0.1));
        cut.WaitForState(() => (api.ListCalls.Count) == 2, TimeSpan.FromSeconds(5));

        busy = false;
        clock.Advance(TimeSpan.FromSeconds(5));
        cut.WaitForState(() => (api.ListCalls.Count) == 3, TimeSpan.FromSeconds(5));
        await Assert.That(clock.ActiveTimers).IsEqualTo(0);

        busy = true;
        api.OnList = _ => ApiResult.Ok(Samples.Page(Samples.Summary(status: "queued")));
        cut.Render();
        ctx.Dispose();
        clock.Advance(TimeSpan.FromSeconds(30));
        await Assert.That(api.ListCalls.Count).IsEqualTo(3);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Page_importButton_isRenderedOnlyForOwners(bool owner)
    {
        var (ctx, _, _) = NewContext(owner);
        using var _c = ctx;

        var cut = Render(ctx);
        cut.WaitForState(() => (cut.FindAll("tbody tr").Count) == 1, TimeSpan.FromSeconds(5));

        await Assert.That(cut.FindAll("fluent-button").Any(b => b.TextContent.Trim() == "Import")).IsEqualTo(owner);
    }
}
