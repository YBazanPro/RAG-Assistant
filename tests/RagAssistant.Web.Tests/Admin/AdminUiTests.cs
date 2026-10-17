using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using RagAssistant.Security;
using RagAssistant.Web.Client.Admin;
using RagAssistant.Web.Client.Chat;
using RagAssistant.Web.Client.Documents;
using RagAssistant.Web.Tests.Chat;
using RagAssistant.Web.Tests.Documents;
using AdminPage = RagAssistant.Web.Client.Pages.Admin;

namespace RagAssistant.Web.Tests.Admin;

// Spec 017, AC-2 and AC-23 to AC-26: the admin page, its tabs and the request inspector.
public class AdminUiTests
{
    private static (BunitContext Ctx, FakeAdminApi Api, FakeDocumentsApi Documents, FakeThreadScroller Scroller) NewContext()
    {
        var ctx = ChatTestData.NewContext(out _, out var scroller);
        var api = new FakeAdminApi();
        var documents = new FakeDocumentsApi();
        ctx.Services.AddSingleton<IAdminApi>(api);
        ctx.Services.AddSingleton<IDocumentsApi>(documents);
        ctx.AddAuthorization().SetAuthorized("maintainer").SetPolicies([PolicyNames.Maintainer]);
        return (ctx, api, documents, scroller);
    }

    private static IRenderedComponent<AdminPage> Render(BunitContext ctx, string query = "")
    {
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/admin" + query);
        return ctx.Render<AdminPage>();
    }

    private static string RowSelector(Guid id) => "#" + RequestsTab.RowId(id);

    // AC-23
    [Test]
    public async Task Overview_showsHealthAndTheSevenDaySummary_andTheWindowSelectorReloadsIt()
    {
        var (ctx, api, _, _) = NewContext();
        using var _c = ctx;

        var cut = Render(ctx);
        cut.WaitForState(() => api.SummaryCalls.Count == 1 && cut.Markup.Contains("Refusal rate"), TimeSpan.FromSeconds(5));

        await Assert.That(api.SummaryCalls.SequenceEqual(["7d"])).IsTrue();
        await Assert.That(cut.Markup).Contains("Corpus health");
        await Assert.That(cut.Markup).Contains("oldest 2 min");
        await Assert.That(cut.Markup).Contains("33.3 %");
        await Assert.That(cut.Markup).Contains("rewrite.timeout");
        var pressed = cut.FindAll("[role=group] fluent-button").Where(b => b.GetAttribute("aria-pressed") == "true").Select(b => b.TextContent.Trim());
        await Assert.That(pressed.SequenceEqual(["7d"])).IsTrue();

        cut.FindAll("[role=group] fluent-button").First(b => b.TextContent.Trim() == "24h").Click();
        cut.WaitForState(() => api.SummaryCalls.Count == 2, TimeSpan.FromSeconds(5));

        await Assert.That(api.SummaryCalls.SequenceEqual(["7d", "24h"])).IsTrue();
    }

    [Test]
    public async Task Overview_withoutRequestsInTheWindow_showsAnEmptyState()
    {
        var (ctx, api, _, _) = NewContext();
        using var _c = ctx;
        api.OnSummary = window => ApiResult.Ok(FakeAdminApi.Samples.Summary(window, requests: 0));

        var cut = Render(ctx);
        cut.WaitForState(() => cut.Markup.Contains("No requests in this window"), TimeSpan.FromSeconds(5));

        await Assert.That(cut.Markup).Contains("No requests in this window");
    }

    // AC-24
    [Test]
    public async Task Requests_openingARowShowsTheInspectorAndPutsTheRequestInTheUrl_andEscapeClosesItAndReturnsFocus()
    {
        var (ctx, api, _, scroller) = NewContext();
        using var _c = ctx;
        var cut = Render(ctx, "?tab=requests");
        cut.WaitForState(() => cut.FindAll(RowSelector(FakeAdminApi.RequestId)).Count == 1, TimeSpan.FromSeconds(5));

        cut.Find(RowSelector(FakeAdminApi.RequestId)).Click();
        cut.WaitForState(() => cut.FindAll(".request-panel").Count == 1 && cut.Markup.Contains("And above that amount?"), TimeSpan.FromSeconds(5));

        var navigation = ctx.Services.GetRequiredService<NavigationManager>();
        await Assert.That(navigation.Uri).Contains($"request={FakeAdminApi.RequestId}");
        await Assert.That(api.DetailCalls.Contains(FakeAdminApi.RequestId)).IsTrue();

        await cut.Find(".request-panel").KeyDownAsync(new KeyboardEventArgs { Key = "Escape" });
        cut.WaitForState(() => cut.FindAll(".request-panel").Count == 0, TimeSpan.FromSeconds(5));

        await Assert.That(navigation.Uri).DoesNotContain("request=");
        await Assert.That(navigation.Uri).Contains("tab=requests");
        await Assert.That(scroller.Focused.Contains(RequestsTab.RowId(FakeAdminApi.RequestId))).IsTrue();
    }

    [Test]
    public async Task Requests_theDeepLinkOpensThePanelDirectly_withEverySectionAndNoHiddenTitle()
    {
        var (ctx, _, _, _) = NewContext();
        using var _c = ctx;

        var cut = Render(ctx, $"?tab=requests&request={FakeAdminApi.RequestId}");
        cut.WaitForState(() => cut.Markup.Contains("Telemetry trace") && cut.Markup.Contains("Budget Policy"), TimeSpan.FromSeconds(5));

        var panel = cut.Find(".request-panel").TextContent;
        foreach (var section in new[] { "Question", "Rewrite", "Retrieval", "Generation", "Result", "Feedback", "Telemetry trace" })
        {
            await Assert.That(panel).Contains(section);
        }

        await Assert.That(panel).Contains("Who approves a budget exception above 10 000?");
        await Assert.That(panel).Contains("cited [1]");
        await Assert.That(panel).Contains("Private or unavailable document");
        await Assert.That(panel).Contains("Wrong threshold");
        await Assert.That(cut.Find("a.open-trace-link").GetAttribute("href")).Contains("0123456789abcdef0123456789abcdef");
        await Assert.That(cut.Find("a.open-trace-link").GetAttribute("rel")).Contains("noopener");
    }

    [Test]
    public async Task Requests_aRequestThatNoLongerExistsSaysSo()
    {
        var (ctx, api, _, _) = NewContext();
        using var _c = ctx;
        api.OnRequest = _ => ApiResult.Fail<RequestDetail>(new ApiError(404, "chat.request_not_found", "The request was not found.", new Dictionary<string, string[]>()));

        var cut = Render(ctx, $"?tab=requests&request={Guid.NewGuid()}");
        cut.WaitForState(() => cut.Markup.Contains("no longer exists"), TimeSpan.FromSeconds(5));

        await Assert.That(cut.Find(".request-panel").TextContent).Contains("This request no longer exists");
    }

    [Test]
    public async Task Requests_filtersAreSentToTheApi_andResetThePage()
    {
        var (ctx, api, _, _) = NewContext();
        using var _c = ctx;
        var cut = Render(ctx, "?tab=requests");
        cut.WaitForState(() => api.RequestCalls.Count == 1, TimeSpan.FromSeconds(5));

        var refused = cut.FindComponents<FluentCheckbox>().First(c => c.Instance.Label == "refused");
        await cut.InvokeAsync(() => refused.Instance.CheckStateChanged.InvokeAsync(true));
        var negative = cut.FindComponents<FluentCheckbox>().First(c => c.Instance.Label == "Rated not helpful or reported");
        await cut.InvokeAsync(() => negative.Instance.CheckStateChanged.InvokeAsync(true));
        cut.WaitForState(() => api.RequestCalls.Count == 3, TimeSpan.FromSeconds(5));

        var last = api.RequestCalls[^1];
        await Assert.That(last.Statuses.SequenceEqual(["refused"])).IsTrue();
        await Assert.That(last.Negative).IsTrue();
        await Assert.That(last.Page).IsEqualTo(1);
    }

    // AC-25
    [Test]
    public async Task Failures_offerRetryOnlyWhereTheApiAllowsIt_andRefreshAfterwards()
    {
        var (ctx, api, documents, _) = NewContext();
        using var _c = ctx;
        var calls = 0;
        var retryable = FakeAdminApi.Samples.Failure("Budget Policy");
        api.OnFailures = _ =>
        {
            calls++;
            return ApiResult.Ok(new PagedResult<FailureItem>(
                calls == 1 ? [retryable, FakeAdminApi.Samples.Failure("Secret plan", "private", canRetry: false)] : [], 1, 25, calls == 1 ? 2 : 0));
        };

        var cut = Render(ctx, "?tab=failures");
        cut.WaitForState(() => cut.FindAll("tbody tr").Count == 2, TimeSpan.FromSeconds(5));

        var retryButtons = cut.FindAll("fluent-button").Where(b => (b.GetAttribute("aria-label") ?? "").StartsWith("Retry ingestion", StringComparison.Ordinal)).ToList();
        await Assert.That(retryButtons.Count).IsEqualTo(1);
        await Assert.That(cut.Markup).Contains("Only the owner can retry");
        await Assert.That(cut.Markup).DoesNotContain("Retry ingestion of Secret plan");

        retryButtons[0].Click();
        cut.WaitForState(() => calls == 2 && cut.Markup.Contains("No failed ingestions"), TimeSpan.FromSeconds(5));

        await Assert.That(documents.RetryCalls.SequenceEqual([retryable.DocumentId])).IsTrue();
        await Assert.That(cut.Markup).Contains("was queued again");
    }

    // AC-26
    [Test]
    public async Task ATabThatFailsShowsItsOwnError_andTheOtherTabsKeepWorking()
    {
        var (ctx, api, _, _) = NewContext();
        using var _c = ctx;
        api.OnFailures = _ => ApiResult.Fail<PagedResult<FailureItem>>(new ApiError(500, "internal", "Something broke.", new Dictionary<string, string[]>()));

        var cut = Render(ctx, "?tab=failures");
        cut.WaitForState(() => cut.Markup.Contains("could not be loaded"), TimeSpan.FromSeconds(5));
        await Assert.That(cut.Find("[role=alert]").TextContent).Contains("Something broke.");

        api.OnFailures = _ => ApiResult.Ok(new PagedResult<FailureItem>([], 1, 25, 0));
        await cut.SelectTabAsync("feedback");
        cut.WaitForState(() => cut.Markup.Contains("Wrong threshold"), TimeSpan.FromSeconds(5));

        await Assert.That(cut.Markup).Contains("Wrong threshold");
        await Assert.That(cut.Markup).DoesNotContain("could not be loaded");
    }

    [Test]
    public async Task Failures_aBlockedSessionAsksToSignInAgain()
    {
        var (ctx, api, _, _) = NewContext();
        using var _c = ctx;
        api.OnFailures = _ => ApiResult.Fail<PagedResult<FailureItem>>(new ApiError(401, "unauthorized", "Sign in.", new Dictionary<string, string[]>()));

        var cut = Render(ctx, "?tab=failures");
        cut.WaitForState(() => cut.Markup.Contains("Sign in required"), TimeSpan.FromSeconds(5));

        await Assert.That(cut.Markup).Contains("Your session has ended");
        await Assert.That(cut.Markup).DoesNotContain("tbody");
    }

    [Test]
    public async Task PrivateDocuments_showMetadataOnly()
    {
        var (ctx, _, _, _) = NewContext();
        using var _c = ctx;

        var cut = Render(ctx, "?tab=private");
        cut.WaitForState(() => cut.FindAll("tbody tr").Count == 1, TimeSpan.FromSeconds(5));

        var row = cut.Find("tbody tr").TextContent;
        await Assert.That(row).Contains("Secret plan");
        await Assert.That(row).Contains("owner-sub");
        await Assert.That(cut.FindAll("tbody tr a, tbody tr fluent-link").Count).IsEqualTo(0);
    }

    [Test]
    public async Task Configuration_showsBothModulesAndNoSecretInput()
    {
        var (ctx, _, _, _) = NewContext();
        using var _c = ctx;

        var cut = Render(ctx, "?tab=configuration");
        cut.WaitForState(() => cut.Markup.Contains("bge-m3") && cut.Markup.Contains("demo-chat"), TimeSpan.FromSeconds(5));

        await Assert.That(cut.Markup).Contains("Prompt version");
        await Assert.That(cut.Markup).Contains("1024 dimensions");
        await Assert.That(cut.FindAll("input, textarea").Count).IsEqualTo(0);
    }

    // AC-2
    [Test]
    public async Task Page_lists_theSixTabs_andLoadsOnlyTheSelectedOne()
    {
        var (ctx, api, _, _) = NewContext();
        using var _c = ctx;

        var cut = Render(ctx, "?tab=feedback");
        cut.WaitForState(() => cut.Markup.Contains("Wrong threshold"), TimeSpan.FromSeconds(5));

        var tabs = cut.FindComponents<FluentTab>().Select(t => t.Instance.Header).ToArray();
        await Assert.That(tabs.SequenceEqual(["Overview", "Requests", "Ingestion failures", "Feedback", "Private documents", "Configuration"])).IsTrue();
        await Assert.That(api.SummaryCalls.Count).IsEqualTo(0);
        await Assert.That(api.RequestCalls.Count).IsEqualTo(0);
    }
}
