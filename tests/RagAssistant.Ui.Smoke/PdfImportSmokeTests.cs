using Microsoft.Playwright;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using static Microsoft.Playwright.Assertions;

namespace RagAssistant.Ui.Smoke;

/// <summary>
/// Spec 010 in a real browser: the upload module (upload.js) reading a real file from the file input, progress, cancel,
/// page numbers on passages and the link to the original. Needs the AppHost running with the embedding model pulled.
/// </summary>
[ClassDataSource<BrowserFixture>(Shared = SharedType.PerTestSession)]
public sealed class PdfImportSmokeTests(BrowserFixture browser)
{
    private const int IngestionTimeoutMs = 240_000;

    private static string TempDirectory(string run)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ragassistant-smoke-pdf-{run}");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>A text-layer PDF whose pages carry <paramref name="linesPerPage"/> lines each, unique to the run.</summary>
    private static byte[] TextPdf(string run, int pages, int linesPerPage)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        for (var p = 1; p <= pages; p++)
        {
            var page = builder.AddPage(PageSize.A4);
            for (var i = 0; i < linesPerPage; i++)
            {
                page.AddText($"Run {run} page {p} sentence {i} explains how flights are booked and approved.", 11, new PdfPoint(50, 700 - (i * 14)), font);
            }
        }

        return builder.Build();
    }

    private static byte[] ScannedPdf()
    {
        var builder = new PdfDocumentBuilder();
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
        for (var p = 0; p < 3; p++)
        {
            builder.AddPage(PageSize.A4).AddPng(png, new PdfRectangle(0, 0, 595, 842));
        }

        return builder.Build();
    }

    [Test]
    public async Task Import_pdf_isIndexedWithPageNumbersAndALinkToTheOriginal()
    {
        var run = Guid.NewGuid().ToString("N")[..8];
        var files = TempDirectory(run);
        var path = Path.Combine(files, $"report-{run}.pdf");
        await File.WriteAllBytesAsync(path, TextPdf(run, pages: 3, linesPerPage: 14));
        try
        {
            var page = await browser.SignInAsync();

            await page.Button("Import").ClickAsync();
            await page.WaitForDialogAsync("Import a document");
            await page.Locator("input[type=file]").SetInputFilesAsync(path);
            await page.Locator("#up-owner input").FillAsync("Smoke");
            await page.Button("Import").ClickAsync();
            await page.WaitForURLAsync("**/documents/*");
            await Expect(page.GetByText("Ingestion completed.")).ToBeVisibleAsync(new() { Timeout = IngestionTimeoutMs });

            // The metadata tab names the format and links the original.
            await Expect(page.GetByText($"report-{run}.pdf · PDF").First).ToBeVisibleAsync();
            var original = page.Locator("a.original-file-link").First;
            await Expect(original).ToHaveTextAsync("Open original");
            await Expect(original).ToHaveAttributeAsync("target", "_blank");

            // Passages carry their pages and an "Open page" link into the stored file.
            await page.GetByRole(AriaRole.Tab, new() { Name = "Passages" }).ClickAsync();
            var first = page.Locator(".passage-card").First;
            await Expect(first.Locator(".passage-pages")).ToContainTextAsync("p. 1");
            var link = first.Locator("a.open-page-link");
            await Expect(link).ToHaveTextAsync("Open page 1");
            await Expect(link).ToHaveAttributeAsync("href", new System.Text.RegularExpressions.Regex(@"api/documents/[0-9a-f-]+/versions/1/file#page=1$"));

            // The link target is the stored PDF, served with the safety headers.
            // Resolved by the browser against the page's base address, exactly as a click would.
            var absolute = await link.EvaluateAsync<string>("e => e.href");
            await Assert.That(absolute).StartsWith($"{browser.BaseUrl}/api/documents/");
            var response = await page.Context.APIRequest.GetAsync(absolute.Split('#')[0], new() { IgnoreHTTPSErrors = true });
            await Assert.That(response.Status).IsEqualTo(200);
            await Assert.That(response.Headers["content-type"]).IsEqualTo("application/pdf");
            await Assert.That(response.Headers["x-content-type-options"]).IsEqualTo("nosniff");
            await Assert.That((await response.BodyAsync())[..5]).IsEquivalentTo("%PDF-"u8.ToArray());
        }
        finally
        {
            Directory.Delete(files, recursive: true);
        }
    }

    [Test]
    public async Task Import_scannedPdf_isRefusedUnderTheFilePickerAndTheFormKeepsItsValues()
    {
        var run = Guid.NewGuid().ToString("N")[..8];
        var files = TempDirectory(run);
        var path = Path.Combine(files, $"scan-{run}.pdf");
        await File.WriteAllBytesAsync(path, ScannedPdf());
        try
        {
            var page = await browser.SignInAsync();

            await page.Button("Import").ClickAsync();
            await page.WaitForDialogAsync("Import a document");
            await page.Locator("input[type=file]").SetInputFilesAsync(path);
            await page.Locator("#up-owner input").FillAsync("Smoke");
            await page.Button("Import").ClickAsync();

            await Expect(page.Locator("#up-file-error")).ToContainTextAsync("looks scanned", new() { Timeout = 30_000 });
            await Assert.That(await page.IsDialogOpenAsync("Import a document")).IsTrue();
            await Expect(page.Locator("#up-owner input")).ToHaveValueAsync("Smoke");
        }
        finally
        {
            Directory.Delete(files, recursive: true);
        }
    }

    [Test]
    public async Task Import_slowUpload_showsProgressAndCancelAbortsItKeepingTheForm()
    {
        var run = Guid.NewGuid().ToString("N")[..8];
        var files = TempDirectory(run);
        var path = Path.Combine(files, $"slow-{run}.pdf");
        // Starts like a PDF so the server would read it; nothing here needs it to be parseable, it is never finished.
        var bytes = new byte[40 * 1024 * 1024];
        "%PDF-1.7\n"u8.CopyTo(bytes);
        await File.WriteAllBytesAsync(path, bytes);
        try
        {
            var page = await browser.SignInAsync();
            var cdp = await page.Context.NewCDPSessionAsync(page);
            await cdp.SendAsync("Network.enable");
            // About 2 MB/s upstream: the 40 MiB body takes ~20 s, long enough to look at and cancel.
            await cdp.SendAsync("Network.emulateNetworkConditions", new Dictionary<string, object>
            {
                ["offline"] = false,
                ["latency"] = 0,
                ["downloadThroughput"] = -1,
                ["uploadThroughput"] = 2_000_000,
            });

            await page.Button("Import").ClickAsync();
            await page.WaitForDialogAsync("Import a document");
            await page.Locator("input[type=file]").SetInputFilesAsync(path);
            await page.Locator("#up-owner input").FillAsync("Smoke");
            await page.Button("Import").ClickAsync();

            var bar = page.Locator("progress[role=progressbar]");
            await Expect(bar).ToBeVisibleAsync(new() { Timeout = 15_000 });
            await Expect(page.GetByText("Uploading").First).ToBeVisibleAsync();
            await Expect(bar).Not.ToHaveAttributeAsync("aria-valuenow", "0", new() { Timeout = 15_000 });
            var progressed = int.Parse(await bar.GetAttributeAsync("aria-valuenow") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
            await Assert.That(progressed).IsLessThan(100);

            await page.Button("Cancel upload").ClickAsync();

            await Expect(bar).ToHaveCountAsync(0);
            await Expect(page.Locator("#up-file-error")).ToHaveCountAsync(0);
            await Assert.That(await page.IsDialogOpenAsync("Import a document")).IsTrue();
            await Expect(page.Locator("#up-owner input")).ToHaveValueAsync("Smoke");
            await Expect(page.Button("Import")).ToBeEnabledAsync();
        }
        finally
        {
            Directory.Delete(files, recursive: true);
        }
    }
}
