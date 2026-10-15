using Microsoft.Playwright;
using TUnit.Core.Interfaces;

namespace RagAssistant.Ui.Smoke;

/// <summary>
/// One Chromium for the whole run. The smoke tests drive an already running AppHost (see the ui-smoke-tests skill);
/// they never start the app themselves.
/// </summary>
public sealed class BrowserFixture : IAsyncInitializer, IAsyncDisposable
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    /// <summary>The Web (BFF) origin, https://localhost:7053 unless RAGASSISTANT_WEB_URL says otherwise.</summary>
    public string BaseUrl { get; } = (Environment.GetEnvironmentVariable("RAGASSISTANT_WEB_URL") ?? "https://localhost:7053").TrimEnd('/');

    public async Task InitializeAsync()
    {
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var probe = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            using var response = await probe.GetAsync($"{BaseUrl}/health");
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new InvalidOperationException(
                $"The app is not reachable at {BaseUrl}. Start it first: dotnet run --project src/RagAssistant.AppHost --launch-profile https", ex);
        }

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = Environment.GetEnvironmentVariable("HEADED") != "1" });
    }

    /// <summary>A fresh, isolated browser context signed in as a dev persona from the realm file.</summary>
    public async Task<IPage> SignInAsync(string username = "owner", string password = "owner-dev")
    {
        var context = await _browser!.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = 1024, Height = 768 },
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/documents");
        await page.FillAsync("#username", username);
        await page.FillAsync("#password", password);
        await page.ClickAsync("#kc-login");
        await page.WaitForURLAsync("**/documents");
        await Assertions.Expect(page.GetByText("Import", new() { Exact = true }).First).ToBeVisibleAsync(new() { Timeout = 60_000 });
        return page;
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.DisposeAsync();
        }

        _playwright?.Dispose();
    }
}
