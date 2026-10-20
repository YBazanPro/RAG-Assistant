using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace RagAssistant.Ui.Smoke;

/// <summary>
/// Spec 020 in a real browser: the home page, asking from a link, rename, export, copy with sources, a citation state badge and the
/// accessible names the accessibility snapshot exposes. Needs the AppHost in Demo chat mode (<c>--launch-profile https-demo</c>).
/// Demo mode suggests no questions (they are "skipped"), so the suggested-question click is covered here through its link
/// (<c>chat?ask=</c>) and by bUnit.
/// </summary>
[ClassDataSource<BrowserFixture>(Shared = SharedType.PerTestSession)]
public sealed class FirstReleaseSmokeTests(BrowserFixture browser)
{
    private const int IngestionTimeoutMs = 90_000;
    private const int AnswerTimeoutMs = 60_000;

    private static readonly SemaphoreSlim ImportLock = new(1, 1);
    private static (string Run, string DocumentUrl)? _document;

    private async Task<(string Run, string DocumentUrl)> EnsureDocumentAsync()
    {
        await ImportLock.WaitAsync();
        try
        {
            if (_document is { } existing)
            {
                return existing;
            }

            var run = Guid.NewGuid().ToString("N")[..8];
            var directory = Path.Combine(Path.GetTempPath(), $"ragassistant-release-smoke-{run}");
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, $"release-smoke-{run}.md");
            await File.WriteAllTextAsync(
                file,
                $"# Mileage policy {run}\n\n## Rates\n\nThe mileage allowance for the release group {run} is forty cents per kilometre, paid monthly " +
                "on the expense report of the following month, provided the trip was approved by the line manager in advance.\n");

            var page = await browser.SignInAsync();
            await page.Button("Import").ClickAsync();
            await page.WaitForDialogAsync("Import a document");
            await page.Locator("input[type=file]").SetInputFilesAsync(file);
            await page.Locator("#up-title input").FillAsync($"Mileage policy {run}");
            await page.Locator("#up-owner input").FillAsync("Smoke");
            await page.Button("Import").ClickAsync();
            await page.WaitForURLAsync("**/documents/*");
            await Expect(page.GetByText("Ingestion completed.")).ToBeVisibleAsync(new() { Timeout = IngestionTimeoutMs });
            var url = page.Url;
            await page.Context.CloseAsync();

            _document = (run, url);
            return _document.Value;
        }
        finally
        {
            ImportLock.Release();
        }
    }

    /// <summary>The names of every button in Chromium's own accessibility tree, which is what a screen reader gets.</summary>
    private static async Task<List<string>> ButtonNamesAsync(IPage page)
    {
        var cdp = await page.Context.NewCDPSessionAsync(page);
        var tree = await cdp.SendAsync("Accessibility.getFullAXTree");
        var names = new List<string>();
        foreach (var node in tree!.Value.GetProperty("nodes").EnumerateArray())
        {
            if (node.TryGetProperty("role", out var role) && role.GetProperty("value").GetString() == "button"
                && node.TryGetProperty("name", out var name) && name.TryGetProperty("value", out var value))
            {
                names.Add(value.GetString() ?? "");
            }
        }

        return names;
    }

    private static string Question(string run) => $"What is the mileage allowance for the release group {run}?";

    /// <summary>Asks through the home page's link form, then waits for the stored answer.</summary>
    private async Task<IPage> AskFromLinkAsync(string question)
    {
        var page = await browser.SignInAsync();
        await page.SetViewportSizeAsync(1280, 900);
        await page.GotoAsync($"{browser.BaseUrl}/chat?ask={Uri.EscapeDataString(question)}");
        await Expect(page.Locator(".citation-list")).ToBeVisibleAsync(new() { Timeout = AnswerTimeoutMs });
        await Expect(page.Button("Send")).ToBeVisibleAsync(new() { Timeout = AnswerTimeoutMs });
        await Expect(page).ToHaveURLAsync(new Regex("/chat/[0-9a-f-]{36}$"));
        return page;
    }

    // AC-7 and AC-26 (Journey 2 starts here)
    [Test]
    public async Task Home_summarisesTheCorpus_andAskAQuestionIsReachedByKeyboard()
    {
        var (run, _) = await EnsureDocumentAsync();
        var page = await browser.SignInAsync();
        await page.GotoAsync(browser.BaseUrl + "/");

        await Expect(page.GetByText("What you can ask about")).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await Expect(page.Locator(".home-corpus-count")).ToContainTextAsync("active document");
        await Expect(page.Locator(".home-recent")).ToContainTextAsync($"Mileage policy {run}");

        // Tab from the top of the page reaches the primary action after the navigation.
        for (var i = 0; i < 30 && !(await page.FocusPathAsync()).Contains("Ask a question", StringComparison.Ordinal); i++)
        {
            await page.Keyboard.PressAsync("Tab");
        }

        await Assert.That(await page.FocusPathAsync()).Contains("Ask a question");
        await page.Keyboard.PressAsync("Enter");
        await Expect(page).ToHaveURLAsync(new Regex("/chat$"));
    }

    // AC-8 (the link the suggested questions use)
    [Test]
    public async Task AskLink_opensANewConversationAndAsksOnce()
    {
        var (run, _) = await EnsureDocumentAsync();
        var page = await AskFromLinkAsync(Question(run));

        await Expect(page.Locator(".thread-question")).ToHaveCountAsync(1);
        await page.ReloadAsync();
        await Expect(page.Locator(".citation-list")).ToBeVisibleAsync(new() { Timeout = AnswerTimeoutMs });
        await Expect(page.Locator(".thread-question")).ToHaveCountAsync(1);
    }

    // AC-15 and AC-26 (focus returns to the trigger)
    [Test]
    public async Task Rename_byKeyboard_escapeCancelsAndEnterSaves()
    {
        var (run, _) = await EnsureDocumentAsync();
        var page = await AskFromLinkAsync(Question(run));
        var title = $"Mileage notes {Guid.NewGuid().ToString("N")[..6]}";

        await page.Locator("#rename-conversation").FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await page.Locator(".rename-input input").FocusAsync();
        await page.Keyboard.PressAsync("Escape");
        await Expect(page.Locator(".rename-input")).ToHaveCountAsync(0);
        await Assert.That(await page.FocusPathAsync()).Contains("Rename");

        await page.Keyboard.PressAsync("Enter");
        await page.Locator(".rename-input input").FillAsync(title);
        await page.Keyboard.PressAsync("Enter");

        await Expect(page.Locator(".conversation-heading")).ToHaveTextAsync(title);
        await Expect(page.Locator(".conversation-link", new() { HasText = title })).ToHaveCountAsync(1);
        await Assert.That(await page.FocusPathAsync()).Contains("Rename");
    }

    // AC-18
    [Test]
    public async Task Export_downloadsAMarkdownFile()
    {
        var (run, _) = await EnsureDocumentAsync();
        var page = await AskFromLinkAsync(Question(run));

        var download = await page.RunAndWaitForDownloadAsync(() => page.Locator(".export-conversation").ClickAsync());
        var path = await download.PathAsync();
        var markdown = await File.ReadAllTextAsync(path!);

        await Assert.That(download.SuggestedFilename).EndsWith(".md");
        await Assert.That(markdown).Contains($"## Question 1\n\n{Question(run)}");
        await Assert.That(markdown).Contains($"- [1] Mileage policy {run}");
    }

    // AC-19
    [Test]
    public async Task CopyAnswerWithSources_putsTheAnswerAndItsSourceListOnTheClipboard()
    {
        var (run, _) = await EnsureDocumentAsync();
        var page = await AskFromLinkAsync(Question(run));
        await page.Context.GrantPermissionsAsync(["clipboard-read", "clipboard-write"], new() { Origin = browser.BaseUrl });

        await page.Button("Copy answer with sources").ClickAsync();
        await Expect(page.Locator(".copy-answer .copy-confirmation")).ToBeVisibleAsync();
        // The Windows clipboard stores CRLF line ends.
        var copied = (await page.EvaluateAsync<string>("() => navigator.clipboard.readText()")).ReplaceLineEndings("\n");

        await Assert.That(copied).Contains("[1]");
        await Assert.That(copied).Contains($"Sources:\n\n- [1] Mileage policy {run} — ");
        await Assert.That(copied).Contains("(version ");
    }

    // AC-10 in the browser, and AC-26's accessible names
    [Test]
    public async Task ReprocessedSource_showsTheNewerVersionBadge_andCitationsHaveAccessibleNames()
    {
        var (run, documentUrl) = await EnsureDocumentAsync();
        var page = await AskFromLinkAsync(Question(run));
        var conversationUrl = page.Url;

        var snapshot = await page.Locator(".chat-main").AriaSnapshotAsync();
        await Assert.That(snapshot).Contains($"button \"Source 1: Mileage policy {run}");
        // Playwright's snapshot does not read the role a Fluent button sets through ElementInternals; Chromium's tree does.
        var buttons = await ButtonNamesAsync(page);
        await Assert.That(buttons).Contains("Copy reference for source 1");
        await Assert.That(buttons.Any(b => b.StartsWith("Delete conversation:", StringComparison.Ordinal))).IsTrue();

        // Reprocessing makes a new version with the same text: the citation now points at an older version, unchanged.
        await page.GotoAsync(documentUrl);
        await page.Button("Reprocess").ClickAsync();
        await page.WaitForDialogAsync("Reprocess");
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.PressAsync("Enter");
        await page.GetByRole(AriaRole.Tab, new() { Name = "History" }).ClickAsync();
        await Expect(page.GetByText("Version 2 (Reprocess)")).ToBeVisibleAsync(new() { Timeout = IngestionTimeoutMs });
        await Expect(page.GetByText("superseded")).ToBeVisibleAsync(new() { Timeout = IngestionTimeoutMs });

        await page.GotoAsync(conversationUrl);
        await Expect(page.Locator(".citation-state").First).ToContainTextAsync("Newer version", new() { Timeout = 30_000 });
        snapshot = await page.Locator(".chat-main").AriaSnapshotAsync();
        await Assert.That(snapshot).Contains("newer version\"");
    }
}
