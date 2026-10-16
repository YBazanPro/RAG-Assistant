using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using RagAssistant.Security;
using RagAssistant.Web.Client.Layout;
using RagAssistant.Web.Client.Security;

namespace RagAssistant.Web.Tests.Components;

public class LayoutComponentTests
{
    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddFluentUIComponents();
        return ctx;
    }

    [Test]
    public async Task NavMenu_menu_button_toggles_the_collapsed_navigation()
    {
        using var ctx = NewContext();
        ctx.AddAuthorization().SetAuthorized("someone").SetPolicies(PolicyNames.KnowledgeSeeker);

        var cut = ctx.Render<NavMenu>();
        var button = cut.Find("fluent-button.nav-toggle");

        await Assert.That(button.GetAttribute("aria-expanded")).IsEqualTo("false");
        await Assert.That(cut.Find("nav").ClassList).DoesNotContain("open");

        button.Click();

        await Assert.That(cut.Find("fluent-button.nav-toggle").GetAttribute("aria-expanded")).IsEqualTo("true");
        await Assert.That(cut.Find("nav").ClassList).Contains("open");
    }

    [Test]
    public async Task NavMenu_nav_is_a_labelled_landmark_and_the_toggle_controls_it()
    {
        using var ctx = NewContext();
        ctx.AddAuthorization().SetAuthorized("someone").SetPolicies(PolicyNames.KnowledgeSeeker);

        var cut = ctx.Render<NavMenu>();

        await Assert.That(cut.Find("nav").GetAttribute("aria-label")).IsEqualTo("Main");
        await Assert.That(cut.Find("fluent-button.nav-toggle").GetAttribute("aria-controls")).IsEqualTo(cut.Find("nav").Id);
    }

    [Test]
    public async Task NavMenu_choosing_a_link_closes_the_menu()
    {
        using var ctx = NewContext();
        ctx.AddAuthorization().SetAuthorized("someone").SetPolicies(PolicyNames.KnowledgeSeeker);
        var cut = ctx.Render<NavMenu>();
        cut.Find("fluent-button.nav-toggle").Click();

        cut.FindAll("a.nav-link").First(a => a.TextContent == "Chat").Click();

        await Assert.That(cut.Find("nav").ClassList).DoesNotContain("open");
    }

    [Test]
    [Arguments(new[] { PolicyNames.KnowledgeSeeker }, false)]
    [Arguments(new[] { PolicyNames.ContentOwner, PolicyNames.KnowledgeSeeker }, false)]
    [Arguments(new[] { PolicyNames.Maintainer, PolicyNames.ContentOwner, PolicyNames.KnowledgeSeeker }, true)]
    public async Task NavMenu_shows_admin_only_with_the_maintainer_policy(string[] policies, bool seesAdmin)
    {
        using var ctx = NewContext();
        ctx.AddAuthorization().SetAuthorized("someone").SetPolicies(policies);

        var cut = ctx.Render<NavMenu>();
        var links = cut.FindAll("a.nav-link").Select(a => a.TextContent).ToArray();

        await Assert.That(links).Contains("Home");
        await Assert.That(links).Contains("Chat");
        await Assert.That(links).Contains("Documents");
        await Assert.That(links.Contains("Admin")).IsEqualTo(seesAdmin);
    }

    private static IRenderedComponent<ThemeMenu> RenderMenu(BunitContext ctx) =>
        ctx.Render<ThemeMenu>(p => p.Add(m => m.UserName, "Owner Dev"));

    private static string Label(AngleSharp.Dom.IElement item) => item.TextContent.Trim();

    // The items raise a custom web-component event bUnit cannot click, so drive the item through its component instance.
    private static Task ChooseAsync(IRenderedComponent<ThemeMenu> cut, string label) =>
        cut.InvokeAsync(() => cut.FindComponents<FluentMenuItem>()
            .Single(i => i.Markup.Contains(label, StringComparison.Ordinal))
            .Instance.CheckedChanged.InvokeAsync(true));

    private static AngleSharp.Dom.IElement Checked(IRenderedComponent<ThemeMenu> cut) =>
        cut.FindAll("fluent-menu-item").Single(i => i.HasAttribute("checked"));

    [Test]
    public async Task ThemeMenu_shows_the_user_name_and_offers_the_three_modes_with_system_checked()
    {
        using var ctx = NewContext();

        var cut = RenderMenu(ctx);

        await Assert.That(cut.Find("#user-menu-button").TextContent).Contains("Owner Dev");
        await Assert.That(cut.FindAll("fluent-menu-item").Select(Label)).IsEquivalentTo(["System theme", "Light theme", "Dark theme"]);
        await Assert.That(Label(Checked(cut))).IsEqualTo("System theme");
    }

    [Test]
    public async Task ThemeMenu_choosing_dark_checks_it_and_applies_the_theme()
    {
        using var ctx = NewContext();
        var cut = RenderMenu(ctx);

        await ChooseAsync(cut, "Dark theme");

        await Assert.That(Label(Checked(cut))).IsEqualTo("Dark theme");
        await Assert.That(ctx.JSInterop.Invocations.Any(i => i.Identifier.Contains("heme", StringComparison.OrdinalIgnoreCase))).IsTrue();
    }

    [Test]
    public async Task ThemeMenu_restores_the_saved_choice_on_load()
    {
        using var ctx = NewContext();
        ctx.JSInterop.Setup<string?>("localStorage.getItem", "ragassistant.theme").SetResult("Dark");

        var cut = RenderMenu(ctx);

        await Assert.That(Label(Checked(cut))).IsEqualTo("Dark theme");
    }

    [Test]
    public async Task ThemeMenu_remembers_the_choice_in_local_storage()
    {
        using var ctx = NewContext();
        var cut = RenderMenu(ctx);

        await ChooseAsync(cut, "Light theme");

        var call = ctx.JSInterop.Invocations.Single(i => i.Identifier == "localStorage.setItem");
        await Assert.That(call.Arguments[0]).IsEqualTo("ragassistant.theme");
        await Assert.That(call.Arguments[1]).IsEqualTo("Light");
    }

    [Test]
    public async Task ThemeMenu_still_works_when_browser_storage_is_blocked()
    {
        using var ctx = NewContext();
        ctx.JSInterop.Setup<string?>("localStorage.getItem", "ragassistant.theme").SetException(new Microsoft.JSInterop.JSException("blocked"));
        ctx.JSInterop.SetupVoid("localStorage.setItem", _ => true).SetException(new Microsoft.JSInterop.JSException("blocked"));

        var cut = RenderMenu(ctx);
        await ChooseAsync(cut, "Dark theme");

        await Assert.That(Label(Checked(cut))).IsEqualTo("Dark theme");
    }

    [Test]
    public async Task RequestedWithHandler_adds_the_header_to_every_request()
    {
        var inner = new CapturingHandler();
        using var invoker = new HttpMessageInvoker(new RequestedWithHandler { InnerHandler = inner });

        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Post, "http://web.test/api/x"), CancellationToken.None);

        await Assert.That(inner.Request!.Headers.GetValues("X-Requested-With").Single()).IsEqualTo("ragassistant");
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage());
        }
    }
}
