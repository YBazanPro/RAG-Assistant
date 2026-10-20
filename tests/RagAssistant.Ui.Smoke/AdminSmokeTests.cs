using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace RagAssistant.Ui.Smoke;

/// <summary>
/// Spec 017 in a real browser: the request inspector's side panel (focus, Escape, deep link). Needs the AppHost running in Demo chat
/// mode (<c>--launch-profile https-demo</c>) and the maintainer dev user; the question asked here is refused or answered by the demo
/// client, either way it leaves a request trace.
/// </summary>
[ClassDataSource<BrowserFixture>(Shared = SharedType.PerTestSession)]
public sealed class AdminSmokeTests(BrowserFixture browser)
{
    private const int AnswerTimeoutMs = 60_000;

    private async Task<(IPage Page, string Question)> AskAsMaintainerAsync()
    {
        var page = await browser.SignInAsync("maintainer", "maintainer-dev");
        var question = $"Admin smoke question {Guid.NewGuid():N}?";
        await page.GotoAsync($"{browser.BaseUrl}/chat");
        await Expect(page.Locator(".scope-summary")).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await page.Locator("fluent-textarea[aria-label='Your question']").ClickAsync();
        await page.Keyboard.TypeAsync(question);
        await page.Keyboard.PressAsync("Enter");
        await Expect(page.GetByText(question).First).ToBeVisibleAsync(new() { Timeout = AnswerTimeoutMs });
        // The trace is written when the answer finishes; the Send button comes back then.
        await Expect(page.Locator("fluent-button", new() { HasTextString = "Send" }).First).ToBeVisibleAsync(new() { Timeout = AnswerTimeoutMs });
        return (page, question);
    }

    [Test]
    public async Task Inspector_opensFromARow_keepsFocusInsideTheUrl_andEscapeReturnsFocusToTheRow()
    {
        var (page, question) = await AskAsMaintainerAsync();
        await page.GotoAsync($"{browser.BaseUrl}/admin?tab=requests");
        var row = page.Locator("fluent-button", new() { HasTextString = question }).First;
        await Expect(row).ToBeVisibleAsync(new() { Timeout = 30_000 });

        await row.ClickAsync();

        var panel = page.Locator("aside.request-panel");
        await Expect(panel).ToBeVisibleAsync();
        await Expect(panel.GetByText("Telemetry trace")).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("request=[0-9a-f-]{36}"));
        await Assert.That(await panel.EvaluateAsync<bool>("p => p.contains(document.activeElement)")).IsTrue();

        await page.Keyboard.PressAsync("Escape");

        await Expect(panel).ToBeHiddenAsync();
        await Expect(page).Not.ToHaveURLAsync(new System.Text.RegularExpressions.Regex("request="));
        await Assert.That(await page.FocusPathAsync()).Contains(question[..8]);
        await page.Context.CloseAsync();
    }

    [Test]
    public async Task Inspector_aDeepLinkOpensThePanelDirectly()
    {
        var (page, question) = await AskAsMaintainerAsync();
        await page.GotoAsync($"{browser.BaseUrl}/admin?tab=requests");
        await page.Locator("fluent-button", new() { HasTextString = question }).First.ClickAsync();
        await Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("request="));
        var deepLink = page.Url;

        await page.GotoAsync(deepLink);

        var panel = page.Locator("aside.request-panel");
        await Expect(panel).ToBeVisibleAsync(new() { Timeout = 30_000 });
        // The question shows twice: as the question and as the retrieval query (spec 014).
        await Expect(panel.GetByText(question).First).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await page.Context.CloseAsync();
    }
}
