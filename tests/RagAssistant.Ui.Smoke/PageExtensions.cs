using Microsoft.Playwright;

namespace RagAssistant.Ui.Smoke;

internal static class PageExtensions
{
    /// <summary>
    /// Fluent buttons have no ARIA role in the DOM, so match their text. The last visible match wins, so a dialog's
    /// button is preferred over the same label on the page behind it.
    /// </summary>
    public static ILocator Button(this IPage page, string text) =>
        page.GetByText(text, new() { Exact = true }).Filter(new() { Visible = true }).Last;

    /// <summary>True while the dialog whose aria-label starts with <paramref name="label"/> is open. Dialogs render lazily, so absent means closed.</summary>
    public static Task<bool> IsDialogOpenAsync(this IPage page, string label) =>
        page.EvaluateAsync<bool>(
            "l => !!document.querySelector(`fluent-dialog[aria-label^=\"${l}\"]`)?.shadowRoot?.querySelector('dialog')?.open", label);

    /// <summary>The focused element through shadow roots, as "TAG:text>TAG:text".</summary>
    public static Task<string> FocusPathAsync(this IPage page) =>
        page.EvaluateAsync<string>(
            "() => { let e = document.activeElement, r = []; while (e) { r.push(e.tagName + ':' + (e.textContent || '').trim().slice(0, 20)); e = e.shadowRoot && e.shadowRoot.activeElement; } return r.join('>'); }");

    public static async Task WaitForDialogAsync(this IPage page, string label, bool open = true)
    {
        for (var i = 0; i < 50; i++)
        {
            if (await page.IsDialogOpenAsync(label) == open)
            {
                return;
            }

            await page.WaitForTimeoutAsync(100);
        }

        throw new TimeoutException($"Dialog '{label}' did not become {(open ? "open" : "closed")}.");
    }
}
