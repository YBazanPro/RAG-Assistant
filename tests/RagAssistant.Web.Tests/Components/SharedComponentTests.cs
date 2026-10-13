using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using RagAssistant.Web.Client.Components;

namespace RagAssistant.Web.Tests.Components;

public class SharedComponentTests
{
    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddFluentUIComponents();
        return ctx;
    }

    [Test]
    public async Task PageHeader_renders_title_description_and_actions()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<PageHeader>(p => p
            .Add(c => c.Title, "Documents")
            .Add(c => c.Description, "The corpus")
            .Add(c => c.Actions, "<button>Import</button>"));

        await Assert.That(cut.Find("h1").TextContent).IsEqualTo("Documents");
        await Assert.That(cut.Markup).Contains("The corpus");
        await Assert.That(cut.Markup).Contains("<button>Import</button>");
    }

    [Test]
    public async Task PageHeader_without_description_or_actions_renders_only_the_title()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<PageHeader>(p => p.Add(c => c.Title, "Chat"));

        await Assert.That(cut.Markup).DoesNotContain("page-header-description");
        await Assert.That(cut.Markup).DoesNotContain("page-header-actions");
    }

    [Test]
    public async Task EmptyState_renders_title_description_icon_and_action()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<EmptyState>(p => p
            .Add(c => c.Title, "No documents yet")
            .Add(c => c.Description, "Import one")
            .Add(c => c.Icon, "<svg id=\"icon\"></svg>")
            .Add(c => c.Action, "<a href=\"/import\">Import</a>"));

        await Assert.That(cut.Find("h2").TextContent).IsEqualTo("No documents yet");
        await Assert.That(cut.Markup).Contains("Import one");
        await Assert.That(cut.Markup).Contains("id=\"icon\"");
        await Assert.That(cut.Markup).Contains("href=\"/import\"");
    }

    [Test]
    public async Task ErrorState_is_an_alert_and_has_no_retry_button_by_default()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<ErrorState>(p => p.Add(c => c.Title, "Failed").Add(c => c.Message, "Boom"));

        await Assert.That(cut.Find("section").GetAttribute("role")).IsEqualTo("alert");
        await Assert.That(cut.Markup).Contains("Boom");
        await Assert.That(cut.FindAll("fluent-button")).IsEmpty();
    }

    [Test]
    public async Task ErrorState_retry_invokes_the_callback()
    {
        using var ctx = NewContext();
        var retries = 0;

        var cut = ctx.Render<ErrorState>(p => p
            .Add(c => c.Title, "Failed")
            .Add(c => c.Message, "Boom")
            .Add(c => c.RetryLabel, "Retry now")
            .Add(c => c.Retry, EventCallback.Factory.Create(this, () => retries++)));

        await Assert.That(cut.Find("fluent-button").TextContent.Trim()).IsEqualTo("Retry now");
        cut.Find("fluent-button").Click();

        await Assert.That(retries).IsEqualTo(1);
    }

    [Test]
    public async Task LoadingState_is_a_status_with_the_default_and_a_custom_label()
    {
        using var ctx = NewContext();

        var defaultLabel = ctx.Render<LoadingState>();
        var custom = ctx.Render<LoadingState>(p => p.Add(c => c.Label, "Loading documents"));

        await Assert.That(defaultLabel.Find("div").GetAttribute("role")).IsEqualTo("status");
        await Assert.That(defaultLabel.Markup).Contains("Loading");
        await Assert.That(custom.Markup).Contains("Loading documents");
    }

    [Test]
    public async Task ConfirmDialog_buttons_invoke_confirm_and_cancel()
    {
        using var ctx = NewContext();
        var confirmed = 0;
        var cancelled = 0;

        var cut = ctx.Render<ConfirmDialog>(p => p
            .Add(c => c.Open, true)
            .Add(c => c.Title, "Archive document")
            .Add(c => c.Message, "This hides it from answers.")
            .Add(c => c.ConfirmLabel, "Archive")
            .Add(c => c.CancelLabel, "Keep")
            .Add(c => c.OnConfirm, EventCallback.Factory.Create(this, () => confirmed++))
            .Add(c => c.OnCancel, EventCallback.Factory.Create(this, () => cancelled++)));

        var buttons = cut.FindAll("fluent-button");
        await Assert.That(cut.Markup).Contains("Archive document");
        await Assert.That(cut.Markup).Contains("This hides it from answers.");

        buttons.Single(b => b.TextContent.Trim() == "Archive").Click();
        buttons.Single(b => b.TextContent.Trim() == "Keep").Click();

        await Assert.That(confirmed).IsEqualTo(1);
        await Assert.That(cancelled).IsEqualTo(1);
    }

    [Test]
    public async Task ConfirmDialog_escape_cancels_and_other_keys_do_not()
    {
        using var ctx = NewContext();
        var cancelled = 0;
        var cut = ctx.Render<ConfirmDialog>(p => p
            .Add(c => c.Open, true)
            .Add(c => c.Title, "Archive")
            .Add(c => c.Message, "Sure?")
            .Add(c => c.OnCancel, EventCallback.Factory.Create(this, () => cancelled++)));

        cut.Find("div").KeyDown(new KeyboardEventArgs { Key = "a" });
        await Assert.That(cancelled).IsEqualTo(0);

        cut.Find("div").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        await Assert.That(cancelled).IsEqualTo(1);
    }

    [Test]
    public async Task ConfirmDialog_destructive_flag_changes_the_confirm_button()
    {
        using var ctx = NewContext();

        var normal = ctx.Render<ConfirmDialog>(p => p.Add(c => c.Title, "T").Add(c => c.Message, "M"));
        var destructive = ctx.Render<ConfirmDialog>(p => p.Add(c => c.Title, "T").Add(c => c.Message, "M").Add(c => c.Destructive, true));

        await Assert.That(normal.Markup).DoesNotContain("confirm-destructive");
        await Assert.That(destructive.Markup).Contains("confirm-destructive");
    }

    [Test]
    public async Task SignInRequired_offers_a_sign_in_link_that_returns_to_the_current_page_without_redirecting()
    {
        using var ctx = NewContext();
        var navigation = ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        navigation.NavigateTo("/documents?page=2");

        var cut = ctx.Render<SignInRequired>();

        await Assert.That(cut.Find("section").GetAttribute("role")).IsEqualTo("alert");
        await Assert.That(cut.Markup).Contains("href=\"bff/login?returnUrl=%2Fdocuments%3Fpage%3D2\"");
        await Assert.That(navigation.Uri).EndsWith("/documents?page=2");
    }
}
