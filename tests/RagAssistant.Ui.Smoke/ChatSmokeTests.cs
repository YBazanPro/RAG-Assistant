using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace RagAssistant.Ui.Smoke;

/// <summary>
/// Spec 013 in a real browser: keyboard flow, focus return, cancel and retry, and the narrow layout.
/// Needs the AppHost running in Demo chat mode (<c>--launch-profile https-demo</c>), the owner dev user and the embedding
/// model, so the document imported here can be retrieved.
/// </summary>
[ClassDataSource<BrowserFixture>(Shared = SharedType.PerTestSession)]
public sealed class ChatSmokeTests(BrowserFixture browser)
{
    private const int IngestionTimeoutMs = 90_000;
    private const int AnswerTimeoutMs = 60_000;

    // One document for the whole class; the first test to need it imports it. The long first sentence keeps the demo
    // client streaming for a second or two, long enough to cancel.
    private static readonly SemaphoreSlim ImportLock = new(1, 1);
    private static string? _run;

    private async Task<string> EnsureDocumentAsync()
    {
        await ImportLock.WaitAsync();
        try
        {
            if (_run is not null)
            {
                return _run;
            }

            var run = Guid.NewGuid().ToString("N")[..8];
            var directory = Path.Combine(Path.GetTempPath(), $"ragassistant-chat-smoke-{run}");
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, $"chat-smoke-{run}.md");
            await File.WriteAllTextAsync(
                file,
                $"# Refund policy {run}\n\nThe refund approval limit for the smoke group {run} is five thousand euros per request, and every request " +
                "above that limit needs the written sign-off of two members of the finance committee before any payment is released to the supplier " +
                "named in the original purchase order, with a copy of the signed approval kept in the department archive.\n");

            var page = await browser.SignInAsync();
            await page.Button("Import").ClickAsync();
            await page.WaitForDialogAsync("Import a document");
            await page.Locator("input[type=file]").SetInputFilesAsync(file);
            await page.Locator("#up-title input").FillAsync($"Refund policy {run}");
            await page.Locator("#up-owner input").FillAsync("Smoke");
            await page.Button("Import").ClickAsync();
            await page.WaitForURLAsync("**/documents/*");
            await Expect(page.GetByText("Ingestion completed.")).ToBeVisibleAsync(new() { Timeout = IngestionTimeoutMs });
            await page.Context.CloseAsync();

            _run = run;
            return run;
        }
        finally
        {
            ImportLock.Release();
        }
    }

    private async Task<IPage> OpenChatAsync(int width = 1280, int height = 900)
    {
        var page = await browser.SignInAsync();
        await page.SetViewportSizeAsync(width, height);
        await page.GotoAsync($"{browser.BaseUrl}/chat");
        await Expect(page.GetByText("Ask about", new() { Exact = false }).First).ToBeVisibleAsync(new() { Timeout = 30_000 });
        return page;
    }

    private static async Task AskAsync(IPage page, string question)
    {
        await page.Locator("fluent-textarea").ClickAsync();
        await page.Keyboard.TypeAsync(question);
        await page.Keyboard.PressAsync("Enter");
    }

    private static Task<string> ActiveElementAsync(IPage page) =>
        page.EvaluateAsync<string>("() => { const e = document.activeElement; return (e.tagName + '#' + e.id + '.' + e.className); }");

    // AC-3 (Shift+Enter), AC-20
    [Test]
    public async Task Ask_byKeyboard_streamsAnswerAndCitationOpensPreviewThatEscapeClosesWithFocusReturned()
    {
        var run = await EnsureDocumentAsync();
        var page = await OpenChatAsync();

        // Shift+Enter adds a line and sends nothing.
        await page.Locator("fluent-textarea").ClickAsync();
        await page.Keyboard.TypeAsync("first line");
        await page.Keyboard.PressAsync("Shift+Enter");
        await page.Keyboard.TypeAsync("second line");
        await Expect(page.Locator(".thread-question")).ToHaveCountAsync(0);
        var typed = await page.Locator("fluent-textarea").EvaluateAsync<string>("e => e.value");
        await Assert.That(typed).Contains("\n");
        await page.Keyboard.PressAsync("Control+A");
        await page.Keyboard.PressAsync("Delete");

        await AskAsync(page, $"What is the refund approval limit for the smoke group {run}?");
        await Expect(page.Locator(".thread-question-text").First).ToContainTextAsync(run);
        await Expect(page.Locator(".citation-list")).ToBeVisibleAsync(new() { Timeout = AnswerTimeoutMs });

        // The thread swaps the streamed answer for the stored one when the stream ends; Send is back once that is done.
        await Expect(page.Button("Send")).ToBeVisibleAsync(new() { Timeout = AnswerTimeoutMs });
        await Expect(page.Locator("button.citation-marker").First).ToBeVisibleAsync();

        // Tab from the thread reaches the citation, Enter opens the preview with the passage.
        await page.Locator(".thread").FocusAsync();
        for (var i = 0; i < 12 && !(await ActiveElementAsync(page)).Contains("citation-marker", StringComparison.Ordinal); i++)
        {
            await page.Keyboard.PressAsync("Tab");
        }

        await Assert.That(await ActiveElementAsync(page)).Contains("citation-marker");
        var openerId = await page.EvaluateAsync<string>("() => document.activeElement.id");
        await page.Keyboard.PressAsync("Enter");
        await Expect(page.Locator("aside.source-preview")).ToBeVisibleAsync();
        await Expect(page.Locator("aside.source-preview pre")).ToContainTextAsync(run);

        // Esc closes it and focus goes back to the citation that opened it.
        await page.Keyboard.PressAsync("Escape");
        await Expect(page.Locator("aside.source-preview")).ToHaveCountAsync(0);
        await Assert.That(await page.EvaluateAsync<string>("() => document.activeElement.id")).IsEqualTo(openerId);
    }

    // AC-21
    [Test]
    public async Task Ask_offCorpus_showsRefusalNoticeWithoutErrorBarOrRetry()
    {
        await EnsureDocumentAsync();
        var page = await OpenChatAsync();

        await AskAsync(page, "What is the airspeed velocity of an unladen swallow in kilometres per second?");

        await Expect(page.Locator(".refusal-notice")).ToBeVisibleAsync(new() { Timeout = AnswerTimeoutMs });
        await Expect(page.Locator("fluent-message-bar")).ToHaveCountAsync(0);
        await Expect(page.Locator(".answer-message").GetByText("Retry")).ToHaveCountAsync(0);
    }

    // AC-22
    [Test]
    public async Task Cancel_duringStream_showsAnswerCancelledAndRetryProducesACompleteAnswer()
    {
        var run = await EnsureDocumentAsync();
        var page = await OpenChatAsync();

        await AskAsync(page, $"What is the refund approval limit for the smoke group {run}?");
        await Expect(page.Locator(".stage-line")).ToContainTextAsync("Writing the answer", new() { Timeout = AnswerTimeoutMs });
        await page.Button("Cancel").ClickAsync();

        await Expect(page.GetByText("Answer cancelled.")).ToBeVisibleAsync(new() { Timeout = AnswerTimeoutMs });
        await page.Button("Retry").ClickAsync();
        await Expect(page.Locator(".citation-list")).ToBeVisibleAsync(new() { Timeout = AnswerTimeoutMs });
    }

    // AC-23
    [Test]
    public async Task NarrowScreen_listOpensFromAButtonAndPreviewIsADrawerThatEscapeCloses()
    {
        var run = await EnsureDocumentAsync();
        var page = await OpenChatAsync(width: 375, height: 812);

        await Expect(page.Locator("nav.conversation-list")).ToHaveCountAsync(0);
        await page.Button("Conversations").ClickAsync();
        await Expect(page.Locator("nav.conversation-list")).ToBeVisibleAsync();
        await page.Button("Conversations").ClickAsync();

        await AskAsync(page, $"What is the refund approval limit for the smoke group {run}?");
        await Expect(page.Locator(".citation-list")).ToBeVisibleAsync(new() { Timeout = AnswerTimeoutMs });
        await Expect(page.Button("Send")).ToBeVisibleAsync(new() { Timeout = AnswerTimeoutMs });
        await page.Locator("button.citation-marker").First.ClickAsync();

        var drawer = page.Locator("aside.source-preview[role=dialog]");
        await Expect(drawer).ToBeVisibleAsync();
        await page.Keyboard.PressAsync("Escape");
        await Expect(drawer).ToHaveCountAsync(0);

        var overflows = await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > document.documentElement.clientWidth");
        await Assert.That(overflows).IsFalse();
    }
}
