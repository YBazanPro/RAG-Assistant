using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;

namespace RagAssistant.Web.Tests.Components;

public class BunitSpikeTests
{
    [Test]
    public async Task FluentButton_renders_content_under_tunit()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddFluentUIComponents();

        var cut = ctx.Render<FluentButton>(parameters => parameters.AddChildContent("Hello"));

        await Assert.That(cut.Markup).Contains("Hello");
    }
}
