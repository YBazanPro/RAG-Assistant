using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using RagAssistant.Web.Client.Chat;

namespace RagAssistant.Web.Tests.Chat;

public sealed class FakeClipboard : IClipboard
{
    public List<string> Copied { get; } = [];

    public bool Blocked { get; set; }

    public Task CopyAsync(string text, CancellationToken cancellationToken)
    {
        if (Blocked)
        {
            throw new ClipboardUnavailableException(new InvalidOperationException("blocked"));
        }

        Copied.Add(text);
        return Task.CompletedTask;
    }
}

public sealed class FakeThreadScroller : IThreadScroller
{
    public List<string> Focused { get; } = [];

    public Task FocusAsync(string elementId, CancellationToken cancellationToken)
    {
        Focused.Add(elementId);
        return Task.CompletedTask;
    }
}

internal static class ChatTestData
{
    public static Citation Citation(int marker = 1, string title = "Budget Exception Policy", string? text = "Exceptions above 5000 need approval.") =>
        new(marker, Guid.Parse("00000000-0000-0000-0000-0000000000a1"), Guid.Parse("00000000-0000-0000-0000-0000000000d1"), Guid.NewGuid(),
            3, title, ["Approval", "Thresholds"], 4, null, text);

    public static BunitContext NewContext(out FakeClipboard clipboard, out FakeThreadScroller scroller)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddFluentUIComponents();
        clipboard = new FakeClipboard();
        scroller = new FakeThreadScroller();
        ctx.Services.AddSingleton<IClipboard>(clipboard);
        ctx.Services.AddSingleton<IThreadScroller>(scroller);
        return ctx;
    }
}
