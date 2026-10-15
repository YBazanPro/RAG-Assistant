using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using RagAssistant.Web.Client.Documents;

namespace RagAssistant.Web.Tests.Documents;

/// <summary>
/// Fluent inputs are web components that raise their value events from JavaScript, which bUnit does not run. Tests therefore
/// drive them the way the component's own JavaScript would: through the parameter callbacks on the component instance.
/// </summary>
internal static class FluentTestHelpers
{
    public static IRenderedComponent<FluentTextInput> TextInput<T>(this IRenderedComponent<T> cut, string label)
        where T : class, IComponent =>
        cut.FindComponents<FluentTextInput>().First(c => c.Instance.Label == label);

    public static string? TextValue<T>(this IRenderedComponent<T> cut, string label)
        where T : class, IComponent =>
        cut.TextInput(label).Instance.Value;

    public static Task SetTextAsync<T>(this IRenderedComponent<T> cut, string label, string value)
        where T : class, IComponent
    {
        var input = cut.TextInput(label);
        return cut.InvokeAsync(() => input.Instance.ValueChanged.InvokeAsync(value));
    }

    public static IRenderedComponent<FluentSelect<FilterChoice, string>> FilterSelect<T>(this IRenderedComponent<T> cut, string label)
        where T : class, IComponent =>
        cut.FindComponents<FluentSelect<FilterChoice, string>>().First(c => c.Instance.Label == label);

    public static Task SetSelectAsync<T>(this IRenderedComponent<T> cut, string label, string value)
        where T : class, IComponent
    {
        var select = cut.FilterSelect(label);
        return cut.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync(value));
    }

    public static Task SelectTabAsync<T>(this IRenderedComponent<T> cut, string tabId)
        where T : class, IComponent
    {
        var tabs = cut.FindComponent<FluentTabs>();
        return cut.InvokeAsync(() => tabs.Instance.ActiveTabIdChanged.InvokeAsync(tabId));
    }
}
