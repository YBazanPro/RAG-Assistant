using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using RagAssistant.Security;
using RagAssistant.Web.Client.Documents;
using RagAssistant.Web.Client.Pages;

namespace RagAssistant.Web.Tests.Documents;

// Spec 008, AC-25 to AC-33 (detail page).
public class DocumentDetailTests
{
    private static readonly Guid Id = Samples.DocId;

    private static (BunitContext Ctx, FakeDocumentsApi Api, FakeEventSource Events, ManualTimeProvider Clock) NewContext(bool owner = false)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddFluentUIComponents();
        var api = new FakeDocumentsApi();
        var events = new FakeEventSource();
        var clock = new ManualTimeProvider();
        ctx.Services.AddSingleton<IDocumentsApi>(api);
        ctx.Services.AddSingleton<IFileUploader>(api.Uploader);
        ctx.Services.AddSingleton<IIngestionEventSource>(events);
        ctx.Services.AddSingleton<TimeProvider>(clock);
        var auth = ctx.AddAuthorization().SetAuthorized("someone");
        auth.SetPolicies(owner ? [PolicyNames.KnowledgeSeeker, PolicyNames.ContentOwner] : [PolicyNames.KnowledgeSeeker]);
        return (ctx, api, events, clock);
    }

    private static IRenderedComponent<DocumentDetail> Render(BunitContext ctx, string query = "")
    {
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo($"http://localhost/documents/{Id}{query}");
        return ctx.Render<DocumentDetail>(p => p.Add(c => c.DocumentId, Id));
    }

    // Timers and stream connections do not render anything, so WaitForState (which re-checks on render) would miss them.
    private static async Task PollAsync(Func<bool> condition)
    {
        for (var i = 0; i < 250 && !condition(); i++)
        {
            await Task.Delay(20);
        }
    }

    private static void WaitForText(IRenderedComponent<DocumentDetail> cut, string text) =>
        cut.WaitForState(() => cut.Markup.Contains(text, StringComparison.Ordinal), TimeSpan.FromSeconds(5));

    [Test]
    public async Task Detail_showsHeaderAndReadOnlyMetadata_andTabsFollowTheUrl()
    {
        var (ctx, _, _, _) = SeekerContext();
        using var _c = ctx;

        var cut = Render(ctx);
        WaitForText(cut, "Budget Exception Policy");

        await Assert.That(cut.Find("h1").TextContent).IsEqualTo("Budget Exception Policy");
        await Assert.That(cut.Find(".status-badge").TextContent).Contains("Active");
        await Assert.That(cut.Find(".detail-summary").TextContent).Contains("Version 1");
        await Assert.That(cut.Find(".meta-card").TextContent).Contains("Finance");
        await Assert.That(cut.Find(".meta-card").TextContent).Contains("user-a");
        // Metadata, passages, suggested questions (spec 020) and history.
        await Assert.That(cut.FindComponents<Microsoft.FluentUI.AspNetCore.Components.FluentTab>().Count).IsEqualTo(4);
        await Assert.That(cut.FindComponent<Microsoft.FluentUI.AspNetCore.Components.FluentTabs>().Instance.ActiveTabId).IsEqualTo("metadata");

        await cut.SelectTabAsync("history");
        await Assert.That(ctx.Services.GetRequiredService<NavigationManager>().Uri).EndsWith("?tab=history");
    }

    private static (BunitContext, FakeDocumentsApi, FakeEventSource, ManualTimeProvider) SeekerContext() => NewContext(owner: false);

    [Test]
    public async Task Detail_unknownDocument_showsNotFoundWithALinkBack()
    {
        var (ctx, api, _, _) = NewContext();
        using var _c = ctx;
        api.OnGet = _ => ApiResult.Fail<DocumentInfo>(new ApiError(404, "documents.not_found", "nope", new Dictionary<string, string[]>()));

        var cut = Render(ctx);
        WaitForText(cut, "Document not found");

        await Assert.That(cut.Markup).Contains("href=\"documents\"");
    }

    [Test]
    public async Task Detail_failedDocument_ownerRetryCallsTheApiAndReloads()
    {
        var (ctx, api, _, _) = NewContext(owner: true);
        using var _c = ctx;
        var failed = Samples.Run("failed", "embedding", error: "boom");
        var retried = false;
        api.OnGet = id => ApiResult.Ok(Samples.Detail(id, "failed", retried ? Samples.Run("queued", "queued", attempt: 2) : failed));
        api.OnRetry = id => { retried = true; return ApiResult.Ok(new RetryAccepted(id, Guid.NewGuid(), Guid.NewGuid(), 2, "queued")); };

        var cut = Render(ctx);
        WaitForText(cut, "Ingestion failed");
        cut.FindAll("fluent-button").First(b => b.TextContent.Trim() == "Retry").Click();

        cut.WaitForState(() => api.RetryCalls.Count == 1, TimeSpan.FromSeconds(5));
        WaitForText(cut, "attempt 2");
        await Assert.That(api.RetryCalls[0]).IsEqualTo(Id);
    }

    [Test]
    public async Task Detail_seekerOrArchived_hasNoRetry()
    {
        var (seekerCtx, seekerApi, _, _) = NewContext(owner: false);
        using var _s = seekerCtx;
        seekerApi.OnGet = id => ApiResult.Ok(Samples.Detail(id, "failed", Samples.Run("failed", "embedding", error: "boom")));
        var seeker = Render(seekerCtx);
        WaitForText(seeker, "Ingestion failed");

        var (ownerCtx, ownerApi, _, _) = NewContext(owner: true);
        using var _o = ownerCtx;
        ownerApi.OnGet = id => ApiResult.Ok(Samples.Detail(id, "archived", Samples.Run("failed", "embedding", error: "boom")));
        var owner = Render(ownerCtx);
        WaitForText(owner, "Ingestion failed");

        await Assert.That(seeker.FindAll("fluent-button").Any(b => b.TextContent.Trim() == "Retry")).IsFalse();
        await Assert.That(owner.FindAll("fluent-button").Any(b => b.TextContent.Trim() == "Retry")).IsFalse();
    }

    [Test]
    public async Task Detail_ownerOnInProgressDocument_advancesTheStepperLiveAndReloadsOnTheTerminalEvent()
    {
        var (ctx, api, events, _) = NewContext(owner: true);
        using var _c = ctx;
        var gets = 0;
        api.OnGet = id =>
        {
            gets++;
            return ApiResult.Ok(Samples.Detail(id, gets == 1 ? "processing" : "active", gets == 1 ? Samples.Run("running", "chunking") : Samples.Run()));
        };
        events.EnqueueEvents(
            Samples.Stage("embedding", "started", "5", "Embedding 2 of 4"),
            Samples.Heartbeat(),
            Samples.Stage("completed", "completed", "6"));

        var cut = Render(ctx);

        cut.WaitForState(() => gets == 2, TimeSpan.FromSeconds(5));
        await Assert.That(events.Opened).IsEqualTo(1);
        await Assert.That(cut.Find(".ingestion-message").GetAttribute("aria-live")).IsEqualTo("polite");
        cut.WaitForState(() => cut.Markup.Contains("Ingestion completed", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Detail_streamFailsRepeatedly_showsLiveUpdatesLostWithARefreshButton()
    {
        var (ctx, api, events, clock) = NewContext(owner: true);
        using var _c = ctx;
        api.OnGet = id => ApiResult.Ok(Samples.Detail(id, "processing", Samples.Run("running", "chunking")));
        for (var i = 0; i < 4; i++)
        {
            events.EnqueueFailure(new HttpRequestException("down"));
        }

        var cut = Render(ctx);
        foreach (var seconds in new[] { 1, 2, 4 })
        {
            await PollAsync(() => clock.ActiveTimers == 1);
            clock.Advance(TimeSpan.FromSeconds(seconds));
        }

        WaitForText(cut, "Live updates lost");
        await Assert.That(events.Opened).IsEqualTo(4);
        await Assert.That(cut.FindAll("fluent-button").Any(b => b.TextContent.Trim() == "Refresh")).IsTrue();
    }

    [Test]
    public async Task Detail_disposed_closesTheStream()
    {
        var (ctx, api, events, _) = NewContext(owner: true);
        api.OnGet = id => ApiResult.Ok(Samples.Detail(id, "processing", Samples.Run("running", "chunking")));
        events.EnqueueHang();
        var cut = Render(ctx);
        cut.WaitForState(() => events.Opened == 1, TimeSpan.FromSeconds(5));

        await ctx.DisposeAsync();

        for (var i = 0; i < 100 && events.Closed == 0; i++)
        {
            await Task.Delay(20);
        }

        await Assert.That(events.Closed).IsEqualTo(1);
    }

    [Test]
    public async Task Detail_seekerOnInProgressDocument_opensNoStreamAndOffersRefresh()
    {
        var (ctx, api, events, _) = NewContext(owner: false);
        using var _c = ctx;
        api.OnGet = id => ApiResult.Ok(Samples.Detail(id, "processing", Samples.Run("running", "chunking")));

        var cut = Render(ctx);
        WaitForText(cut, "Progress is shown as of page load");

        await Assert.That(events.Opened).IsEqualTo(0);
        await Assert.That(cut.FindAll("fluent-button").Any(b => b.TextContent.Trim() == "Refresh")).IsTrue();
    }

    [Test]
    public async Task Passages_defaultToTheActiveVersion_collapseLongTextAndEncodeMarkup()
    {
        var (ctx, api, _, _) = NewContext();
        using var _c = ctx;
        var longText = "<script>alert(1)</script>" + new string('x', 400);
        api.OnVersions = _ => ApiResult.Ok(new VersionList([Samples.Version(2, status: "failed"), Samples.Version(1, isActive: true)]));
        api.OnPassages = (version, page, _) => ApiResult.Ok(new PassagePage(version, "indexed", true, [Samples.Passage(0, longText)], page, 50, 1));

        var cut = Render(ctx, "?tab=passages");
        cut.WaitForState(() => cut.FindAll(".passage-card").Count == 1, TimeSpan.FromSeconds(5));

        await Assert.That(api.PassageCalls[0].Version).IsEqualTo(1);
        await Assert.That(cut.Markup).Contains("Version 1 (active)");
        await Assert.That(cut.Find(".passage-text-collapsed").TextContent.Length).IsEqualTo(PassageCardPreview + 1);
        await Assert.That(cut.Find(".breadcrumb").TextContent).Contains("Handbook");

        cut.Find(".passage-card fluent-button").Click();

        await Assert.That(cut.Find("pre.passage-text").TextContent).IsEqualTo(longText);
        await Assert.That(cut.Markup).DoesNotContain("<script>");
        await Assert.That(cut.Markup).Contains("&lt;script&gt;");
    }

    private const int PassageCardPreview = 300;

    [Test]
    public async Task Passages_deepLink_requestsFocusAndSelectsTheCard()
    {
        var (ctx, api, _, _) = NewContext();
        using var _c = ctx;
        var focus = Guid.NewGuid();
        api.OnVersions = _ => ApiResult.Ok(new VersionList([Samples.Version(2, isActive: true), Samples.Version(1)]));
        api.OnPassages = (version, _, _) => ApiResult.Ok(new PassagePage(version, "indexed", true, [Samples.Passage(0), Samples.Passage(1, id: focus)], 2, 50, 100));

        var cut = Render(ctx, $"?tab=passages&version=2&passage={focus}");
        cut.WaitForState(() => cut.FindAll(".passage-selected").Count == 1, TimeSpan.FromSeconds(5));

        await Assert.That(api.PassageCalls[0].Focus).IsEqualTo(focus);
        await Assert.That(api.PassageCalls[0].Version).IsEqualTo(2);
        await Assert.That(cut.Find(".passage-selected").GetAttribute("id")).IsEqualTo($"passage-{focus}");
        await Assert.That(cut.FindAll(".passage-card").Count).IsEqualTo(2);
    }

    [Test]
    public async Task Passages_unknownDeepLinkedPassage_showsANoticeAndTheFirstPage()
    {
        var (ctx, api, _, _) = NewContext();
        using var _c = ctx;
        api.OnVersions = _ => ApiResult.Ok(new VersionList([Samples.Version(1, isActive: true)]));
        api.OnPassages = (version, page, focus) => focus is null
            ? ApiResult.Ok(Samples.Passages(version, 1, 2))
            : ApiResult.Fail<PassagePage>(new ApiError(404, "documents.passage_not_found", "no", new Dictionary<string, string[]>()));

        var cut = Render(ctx, $"?tab=passages&passage={Guid.NewGuid()}");
        cut.WaitForState(() => cut.FindAll(".passage-card").Count == 2, TimeSpan.FromSeconds(5));

        await Assert.That(cut.Markup).Contains("That passage is not in this version");
        await Assert.That(cut.FindAll(".passage-selected").Count).IsEqualTo(0);
    }

    [Test]
    public async Task Passages_versionWithoutPassages_showsTheEmptyState()
    {
        var (ctx, api, _, _) = NewContext();
        using var _c = ctx;
        api.OnPassages = (version, page, _) => ApiResult.Ok(new PassagePage(version, "failed", false, [], page, 50, 0));

        var cut = Render(ctx, "?tab=passages");
        WaitForText(cut, "Ingestion has not finished or failed");

        await Assert.That(cut.FindAll(".passage-card").Count).IsEqualTo(0);
    }

    [Test]
    public async Task History_listsVersionsWithRunsErrorsAndTheActiveMarker()
    {
        var (ctx, api, _, _) = NewContext();
        using var _c = ctx;
        api.OnVersions = _ => ApiResult.Ok(new VersionList(
        [
            Samples.Version(2, status: "failed", runs: [Samples.Run("failed", "embedding", attempt: 2, error: "model unavailable"), Samples.Run("succeeded", "completed")]),
            Samples.Version(1, isActive: true),
        ]));

        var cut = Render(ctx, "?tab=history");
        cut.WaitForState(() => cut.FindAll(".version-table tbody tr").Count == 2, TimeSpan.FromSeconds(5));

        var text = cut.Find(".version-table").TextContent;
        await Assert.That(text).Contains("model unavailable");
        await Assert.That(text).Contains("embedding");
        await Assert.That(text).Contains("policy.md");
        await Assert.That(cut.FindAll(".active-chip").Count).IsEqualTo(1);
        await Assert.That(cut.FindAll(".run-list li").Count).IsEqualTo(3);
    }

    [Test]
    public async Task Detail_failedReingestionWithOlderActiveVersion_showsFailedBadgeButPassagesOfTheActiveVersion()
    {
        var (ctx, api, _, _) = NewContext();
        using var _c = ctx;
        api.OnGet = id => ApiResult.Ok(Samples.Detail(id, "failed", Samples.Run("failed", "embedding", attempt: 1, error: "boom")));
        api.OnVersions = _ => ApiResult.Ok(new VersionList([Samples.Version(2, status: "failed"), Samples.Version(1, isActive: true)]));

        var cut = Render(ctx, "?tab=passages");
        cut.WaitForState(() => api.PassageCalls.Count > 0, TimeSpan.FromSeconds(5));

        await Assert.That(cut.Find(".status-badge").TextContent).Contains("Failed");
        await Assert.That(api.PassageCalls[0].Version).IsEqualTo(1);
    }
}
