using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace RagAssistant.Ui.Smoke;

/// <summary>
/// Spec 015 in a real browser: the wide chat layout, the scope picker (dialog, focus, keyboard) and the activity panel.
/// Needs the AppHost running in Demo chat mode (<c>--launch-profile https-demo</c>) and the owner dev user.
/// </summary>
[ClassDataSource<BrowserFixture>(Shared = SharedType.PerTestSession)]
public sealed class ChatScopeSmokeTests(BrowserFixture browser)
{
    private const int IngestionTimeoutMs = 90_000;
    private const int AnswerTimeoutMs = 60_000;

    private static readonly SemaphoreSlim ImportLock = new(1, 1);
    private static string? _title;

    // The picker needs an active document to choose; this class imports its own so it does not depend on another test's data.
    private async Task<string> EnsureDocumentAsync()
    {
        await ImportLock.WaitAsync();
        try
        {
            if (_title is not null)
            {
                return _title;
            }

            var run = Guid.NewGuid().ToString("N")[..8];
            var directory = Path.Combine(Path.GetTempPath(), $"ragassistant-scope-smoke-{run}");
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, $"scope-smoke-{run}.md");
            await File.WriteAllTextAsync(file, $"# Scope policy {run}\n\nThe scope smoke group {run} allows lunch expenses up to thirty euros per person per day.\n");

            var page = await browser.SignInAsync();
            await page.Button("Import").ClickAsync();
            await page.WaitForDialogAsync("Import a document");
            await page.Locator("input[type=file]").SetInputFilesAsync(file);
            await page.Locator("#up-title input").FillAsync($"Scope policy {run}");
            await page.Locator("#up-owner input").FillAsync("Smoke");
            await page.Button("Import").ClickAsync();
            await page.WaitForURLAsync("**/documents/*");
            await Expect(page.GetByText("Ingestion completed.")).ToBeVisibleAsync(new() { Timeout = IngestionTimeoutMs });
            await page.Context.CloseAsync();

            _title = $"Scope policy {run}";
            return _title;
        }
        finally
        {
            ImportLock.Release();
        }
    }

    private async Task<IPage> OpenChatAsync(int width, int height)
    {
        var page = await browser.SignInAsync();
        await page.SetViewportSizeAsync(width, height);
        await page.GotoAsync($"{browser.BaseUrl}/chat");
        await Expect(page.Locator(".scope-summary")).ToBeVisibleAsync(new() { Timeout = 30_000 });
        return page;
    }

    private static async Task AskAsync(IPage page, string question)
    {
        await page.Locator("fluent-textarea[aria-label='Your question']").ClickAsync();
        await page.Keyboard.TypeAsync(question);
        await page.Keyboard.PressAsync("Enter");
    }

    // AC-15, AC-16
    [Test]
    public async Task Layout_onAWideScreen_usesTheWidthKeepsAReadableColumnAndPutsUserMessagesOnTheRight()
    {
        var page = await OpenChatAsync(1920, 1000);
        await AskAsync(page, "What is the capital of Australia?");
        await Expect(page.Locator(".thread-answer").First).ToBeVisibleAsync(new() { Timeout = AnswerTimeoutMs });
        await Expect(page.Button("Send")).ToBeVisibleAsync(new() { Timeout = AnswerTimeoutMs });

        var main = await page.Locator(".app-main").BoundingBoxAsync();
        var thread = await page.Locator(".thread").BoundingBoxAsync();
        var answer = await page.Locator(".thread-answer").First.BoundingBoxAsync();
        var bubble = await page.Locator(".thread-bubble").First.BoundingBoxAsync();

        await Assert.That(main!.Width).IsGreaterThan(1100);
        await Assert.That(answer!.Width).IsLessThanOrEqualTo(840);
        var center = thread!.X + (thread.Width / 2);
        await Assert.That(bubble!.X + bubble.Width).IsGreaterThan(center + 100); // right-aligned
        await Assert.That(answer.X).IsLessThan(bubble.X); // the assistant column starts further left
        await Expect(page.Locator(".thread-question .visually-hidden").First).ToHaveTextAsync("You said");
        await Expect(page.Locator(".thread-answer .visually-hidden").First).ToHaveTextAsync("Assistant said");
    }

    // AC-17, AC-11, AC-12
    [Test]
    public async Task Picker_keyboardAndFocus_thenTheScopeShowsInTheActivityPanel()
    {
        var title = await EnsureDocumentAsync();
        var page = await OpenChatAsync(1280, 900);

        // Enter opens the picker, Escape closes it and focus goes back to the button that opened it.
        await page.Locator(".scope-choose").FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForDialogAsync("Choose what to search");
        await Expect(page.GetByText(title).First).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await Assert.That(await page.FocusPathAsync()).Contains("DIALOG");
        await page.Keyboard.PressAsync("Escape");
        await page.WaitForDialogAsync("Choose what to search", open: false);
        await Assert.That(await page.FocusPathAsync()).Contains("Choose documents");

        // Choose the document and apply.
        await page.Locator(".scope-choose").ClickAsync();
        await page.WaitForDialogAsync("Choose what to search");
        await page.Locator("#scope-search input").FillAsync(title);
        await page.WaitForTimeoutAsync(1200); // the search is debounced; the list is replaced while it loads
        await Expect(page.Locator("[aria-label='Documents'] fluent-checkbox")).ToHaveCountAsync(1);
        var checkbox = page.Locator("[aria-label='Documents'] fluent-checkbox").First;
        await checkbox.FocusAsync();
        await page.Keyboard.PressAsync("Space");
        await Expect(page.Locator(".scope-picker")).ToContainTextAsync("1 of 25 documents");
        await page.Button("Apply").ClickAsync();
        await page.WaitForDialogAsync("Choose what to search", open: false);
        await Expect(page.Locator(".scope-chip", new() { HasText = title })).ToBeVisibleAsync();
        await Expect(page.Locator(".scope-summary")).ToContainTextAsync("Searching 1 document");

        // The question is searched under that scope and the panel says so.
        await AskAsync(page, "How much may lunch cost?");
        await Expect(page.Button("Send")).ToBeVisibleAsync(new() { Timeout = AnswerTimeoutMs });
        await Expect(page.Locator(".activity-toggle")).ToBeVisibleAsync();
        await page.Locator(".activity-toggle").ClickAsync();
        await Expect(page.Locator(".activity-body")).ToContainTextAsync("1 document", new() { Timeout = 15_000 });
        await Expect(page.Locator(".activity-body")).ToContainTextAsync(title);
        await Expect(page.Locator(".activity-toggle")).ToHaveAttributeAsync("aria-expanded", "true");

        // The scope is saved on the conversation: it is still there after a reload.
        await page.ReloadAsync();
        await Expect(page.Locator(".scope-chip", new() { HasText = title })).ToBeVisibleAsync(new() { Timeout = 30_000 });
    }
}
