using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using RagAssistant.Web.Client.Chat;
using RagAssistant.Web.Client.Documents;

namespace RagAssistant.Web.Tests.Chat;

// Spec 014, AC-16 and AC-17: rating and issue reports under a finished answer.
public class FeedbackUiTests
{
    private static (BunitContext Ctx, FakeChatApi Api) NewContext()
    {
        var ctx = ChatTestData.NewContext(out _, out _);
        var api = new FakeChatApi();
        ctx.Services.AddSingleton<IChatApi>(api);
        return (ctx, api);
    }

    private static ChatMessage Answer(string status, string? rating = null) =>
        AnswerData.Assistant(status, text: "An answer.") with { Rating = rating, ReportCount = 0 };

    private static IRenderedComponent<AnswerFeedback> Render(BunitContext ctx, ChatMessage message, List<string>? announced = null) =>
        ctx.Render<AnswerFeedback>(p => p
            .Add(c => c.ConversationId, Guid.NewGuid())
            .Add(c => c.Message, message)
            .Add(c => c.OnAnnounce, EventCallback.Factory.Create<string>(ctx, s => announced?.Add(s))));

    private static AngleSharp.Dom.IElement Button(IRenderedComponent<AnswerFeedback> cut, string text) =>
        cut.FindAll("fluent-button").First(b => b.TextContent.Trim() == text);

    // AC-16
    [Test]
    [Arguments("complete")]
    [Arguments("partial")]
    [Arguments("refused")]
    public async Task Controls_areShownOnFinishedAnswers(string status)
    {
        var (ctx, _) = NewContext();
        await using var _c = ctx;

        var cut = Render(ctx, Answer(status));

        await Assert.That(cut.FindAll(".answer-feedback").Count).IsEqualTo(1);
    }

    // AC-16
    [Test]
    [Arguments("failed")]
    [Arguments("cancelled")]
    [Arguments("pending")]
    public async Task Controls_areHiddenOnAnswersThatAreNotFinished(string status)
    {
        var (ctx, _) = NewContext();
        await using var _c = ctx;

        var cut = Render(ctx, Answer(status));

        await Assert.That(cut.FindAll(".answer-feedback").Count).IsEqualTo(0);
    }

    // AC-16
    [Test]
    public async Task Controls_areHiddenWhileTheAnswerStreams()
    {
        var (ctx, _) = NewContext();
        await using var _c = ctx;
        using var live = new LiveAnswer(new Documents.ManualTimeProvider());

        var cut = ctx.Render<AnswerMessage>(p => p.Add(c => c.Live, live).Add(c => c.ConversationId, Guid.NewGuid()));

        await Assert.That(cut.FindAll(".answer-feedback").Count).IsEqualTo(0);
    }

    // AC-16
    [Test]
    public async Task Rating_reflectsAriaPressed_setsOnClickAndClearsOnSecondClick()
    {
        var (ctx, api) = NewContext();
        await using var _c = ctx;
        var announced = new List<string>();
        var message = Answer("complete", rating: "not_helpful");

        var cut = Render(ctx, message, announced);
        await Assert.That(Button(cut, "Not helpful").GetAttribute("aria-pressed")).IsEqualTo("true");
        await Assert.That(Button(cut, "Helpful").GetAttribute("aria-pressed")).IsEqualTo("false");

        Button(cut, "Helpful").Click();
        cut.WaitForState(() => Button(cut, "Helpful").GetAttribute("aria-pressed") == "true", TimeSpan.FromSeconds(5));
        await Assert.That(api.RatingCalls).IsEquivalentTo([(message.Id, "helpful")]);
        await Assert.That(Button(cut, "Not helpful").GetAttribute("aria-pressed")).IsEqualTo("false");

        Button(cut, "Helpful").Click();
        cut.WaitForState(() => Button(cut, "Helpful").GetAttribute("aria-pressed") == "false", TimeSpan.FromSeconds(5));
        await Assert.That(api.ClearCalls).IsEquivalentTo([message.Id]);
        await Assert.That(announced).IsEquivalentTo(["Rated helpful", "Rating removed"]);
    }

    [Test]
    public async Task Rating_failure_isShownNextToTheControlAndKeepsTheOldState()
    {
        var (ctx, api) = NewContext();
        await using var _c = ctx;
        api.OnRate = _ => ApiResult.Fail<RatingResult>(new ApiError(500, "x", "boom", new Dictionary<string, string[]>()));

        var cut = Render(ctx, Answer("complete"));
        Button(cut, "Helpful").Click();
        cut.WaitForState(() => cut.FindAll(".feedback-error").Count == 1, TimeSpan.FromSeconds(5));

        await Assert.That(Button(cut, "Helpful").GetAttribute("aria-pressed")).IsEqualTo("false");
    }

    // AC-17
    [Test]
    public async Task Report_requiresACategory_thenSubmitsAndThanks()
    {
        var (ctx, api) = NewContext();
        await using var _c = ctx;
        var announced = new List<string>();
        var message = Answer("complete");

        var cut = Render(ctx, message, announced);
        Button(cut, "Report issue").Click();
        Button(cut, "Submit").Click();
        await Assert.That(api.ReportCalls.Count).IsEqualTo(0);
        await Assert.That(cut.Markup).Contains("Choose a category.");

        var select = cut.FindComponent<FluentSelect<CategoryChoice, string>>();
        await cut.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync("wrong_source"));
        await cut.InvokeAsync(() => cut.FindComponent<FluentTextArea>().Instance.ValueChanged.InvokeAsync("  The second source is another policy.  "));
        Button(cut, "Submit").Click();
        cut.WaitForState(() => cut.FindAll(".feedback-thanks").Count == 1, TimeSpan.FromSeconds(5));

        await Assert.That(api.ReportCalls).IsEquivalentTo([(message.Id, "wrong_source", (string?)"The second source is another policy.")]);
        await Assert.That(cut.Find(".feedback-thanks").TextContent).IsEqualTo("Thanks, reported.");
        await Assert.That(announced).IsEquivalentTo(["Issue reported"]);
        await Assert.That(Button(cut, "Report issue").HasAttribute("disabled")).IsFalse();
    }

    // AC-17
    [Test]
    public async Task Report_atTheLimit_showsAnInlineMessageAndDisablesFurtherReports()
    {
        var (ctx, api) = NewContext();
        await using var _c = ctx;
        api.OnReport = _ => ApiResult.Fail<IssueReportResult>(new ApiError(409, "chat.report_limit", "limit", new Dictionary<string, string[]>()));

        var cut = Render(ctx, Answer("complete"));
        Button(cut, "Report issue").Click();
        var select = cut.FindComponent<FluentSelect<CategoryChoice, string>>();
        await cut.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync("other"));
        Button(cut, "Submit").Click();
        cut.WaitForState(() => cut.Markup.Contains("maximum number of reports", StringComparison.Ordinal), TimeSpan.FromSeconds(5));

        await Assert.That(cut.FindAll(".feedback-thanks").Count).IsEqualTo(0);
        await Assert.That(Button(cut, "Report issue").HasAttribute("disabled")).IsTrue();
    }
}
