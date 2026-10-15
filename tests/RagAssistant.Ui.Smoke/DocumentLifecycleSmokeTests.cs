using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace RagAssistant.Ui.Smoke;

/// <summary>
/// Spec 009 in a real browser: what bUnit's loose JavaScript mode cannot show (real file streams, dialogs, focus, Escape).
/// Needs the AppHost running, the owner dev user and the embedding model, so ingestion can finish.
/// </summary>
[ClassDataSource<BrowserFixture>(Shared = SharedType.PerTestSession)]
public sealed class DocumentLifecycleSmokeTests(BrowserFixture browser)
{
    private const int IngestionTimeoutMs = 90_000;

    [Test]
    public async Task Import_dialog_escapeClosesAndCancelRestoresFocus()
    {
        var page = await browser.SignInAsync();
        var trigger = page.GetByText("Import", new() { Exact = true }).First;

        await trigger.ClickAsync();
        await page.WaitForDialogAsync("Import a document");
        await page.Keyboard.PressAsync("Escape");
        await page.WaitForDialogAsync("Import a document", open: false);

        await trigger.ClickAsync();
        await page.WaitForDialogAsync("Import a document");
        await page.Button("Cancel").ClickAsync();
        await page.WaitForDialogAsync("Import a document", open: false);

        await Assert.That(await page.FocusPathAsync()).Contains("Import");
    }

    [Test]
    public async Task Lifecycle_importEditConflictNewVersionReprocessArchiveRestore()
    {
        var run = Guid.NewGuid().ToString("N")[..8];
        var title = $"Smoke {run}";
        var files = Path.Combine(Path.GetTempPath(), $"ragassistant-smoke-{run}");
        Directory.CreateDirectory(files);
        var v1 = Path.Combine(files, $"smoke-{run}.md");
        var v2 = Path.Combine(files, $"smoke-{run}-v2.md");
        await File.WriteAllTextAsync(v1, $"# Smoke {run}\n\nFlights must be booked 14 days ahead ({run}).\n");
        await File.WriteAllTextAsync(v2, $"# Smoke {run}\n\nFlights must be booked 21 days ahead ({run}).\n");

        try
        {
            var page = await browser.SignInAsync();

            // Import through the dialog with a real file; success navigates to the detail page.
            await page.Button("Import").ClickAsync();
            await page.WaitForDialogAsync("Import a document");
            await page.Locator("input[type=file]").SetInputFilesAsync(v1);
            await page.Locator("#up-title input").FillAsync(title);
            await page.Locator("#up-owner input").FillAsync("Smoke");
            await page.Button("Import").ClickAsync();
            await page.WaitForURLAsync("**/documents/*");
            await Expect(page.GetByText("Ingestion completed.")).ToBeVisibleAsync(new() { Timeout = IngestionTimeoutMs });
            var detailUrl = page.Url;

            // A second tab holds the old revision; saving there must conflict (412).
            var stale = await page.Context.NewPageAsync();
            await stale.GotoAsync(detailUrl);
            await stale.Button("Edit").ClickAsync();

            await page.Button("Edit").ClickAsync();
            await page.Locator("#md-title input").FillAsync(title + " renamed");
            await page.Button("Save").ClickAsync();
            await Expect(page.GetByText("Changes saved.")).ToBeVisibleAsync();

            await stale.Locator("#md-title input").FillAsync(title + " conflicting");
            await stale.Button("Save").ClickAsync();
            await Expect(stale.GetByText("This document was changed by someone else.")).ToBeVisibleAsync();
            await stale.Button("Reload").ClickAsync();
            await Expect(stale.GetByText(title + " renamed").First).ToBeVisibleAsync();
            await stale.CloseAsync();

            // New version through the dialog supersedes version 1; Escape closes the dialog first.
            await page.Button("Upload new version").ClickAsync();
            await page.WaitForDialogAsync("Upload a new version");
            await page.Keyboard.PressAsync("Escape");
            await page.WaitForDialogAsync("Upload a new version", open: false);
            await page.Button("Upload new version").ClickAsync();
            await page.WaitForDialogAsync("Upload a new version");
            await page.Locator("input[type=file]").SetInputFilesAsync(v2);
            await page.Button("Upload").ClickAsync();
            await page.WaitForDialogAsync("Upload a new version", open: false);
            await page.GetByRole(AriaRole.Tab, new() { Name = "History" }).ClickAsync();
            await Expect(page.GetByText("Version 2 (Upload)")).ToBeVisibleAsync();
            await Expect(page.GetByText("superseded")).ToBeVisibleAsync(new() { Timeout = IngestionTimeoutMs });

            // Reprocess is confirmed first; Escape cancels and sends nothing.
            await page.Button("Reprocess").ClickAsync();
            await page.WaitForDialogAsync("Reprocess");
            await page.Keyboard.PressAsync("Escape");
            await page.WaitForDialogAsync("Reprocess", open: false);
            await Expect(page.GetByText("Version 3")).ToHaveCountAsync(0);
            await page.Button("Reprocess").ClickAsync();
            await page.WaitForDialogAsync("Reprocess");
            await page.Keyboard.PressAsync("Tab");
            await page.Keyboard.PressAsync("Enter");
            await Expect(page.GetByText("Version 3 (Reprocess)")).ToBeVisibleAsync(new() { Timeout = IngestionTimeoutMs });
            await Expect(page.GetByText("Version 1 (Upload)")).ToBeVisibleAsync();

            // Archive (Cancel returns focus to the trigger), then restore.
            await page.Button("Archive").ClickAsync();
            await page.WaitForDialogAsync("Archive");
            await page.Button("Cancel").ClickAsync();
            await page.WaitForDialogAsync("Archive", open: false);
            await Assert.That(await page.FocusPathAsync()).Contains("Archive");
            await page.Button("Archive").ClickAsync();
            await page.WaitForDialogAsync("Archive");
            await page.Keyboard.PressAsync("Tab");
            await page.Keyboard.PressAsync("Enter");
            await Expect(page.GetByText("Archived").First).ToBeVisibleAsync();
            await page.Button("Restore").ClickAsync();
            await page.WaitForDialogAsync("Restore");
            await page.Keyboard.PressAsync("Tab");
            await page.Keyboard.PressAsync("Enter");
            await Expect(page.GetByText("Active").First).ToBeVisibleAsync();
        }
        finally
        {
            Directory.Delete(files, recursive: true);
        }
    }
}
