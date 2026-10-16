using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using RagAssistant.Web.Client.Chat;
using RagAssistant.Web.Client.Documents;
using RagAssistant.Web.Tests.Documents;

namespace RagAssistant.Web.Tests.Chat;

// Spec 015, AC-11 to AC-14: the activity panel, live and stored.
public class ActivityPanelTests
{
    private static ActivityView Stored(bool modelCalled = true, string status = "complete", string? refusal = null) => new(
        "Who approves a budget exception?",
        new RewriteStep(false, "Who approves a budget exception?", null, null),
        new ScopeStep(null, ["finance"], [new ScopeDocument(Guid.NewGuid(), "Budget Policy", true), new ScopeDocument(Guid.NewGuid(), "Old Policy", false)]),
        new RetrievalStep(
            modelCalled ? "sufficient" : "noEvidence", 0.55, 0.6, 12,
            [new PassageStep(1, 1, 0.91, true, Guid.NewGuid(), "Budget Policy", 2, ["Approval"], 3, 3), new PassageStep(2, 2, 0.58, false, null, null, null, [], null, null)]),
        new AnswerStep(modelCalled, status, refusal, null, 210, 950, 0, false));

    private static (BunitContext Ctx, FakeChatApi Api) NewContext()
    {
        var ctx = ChatTestData.NewContext(out _, out _);
        var api = new FakeChatApi();
        ctx.Services.AddSingleton<IChatApi>(api);
        return (ctx, api);
    }

    private static IRenderedComponent<ActivityPanel> RenderStored(BunitContext ctx, bool open = false) => ctx.Render<ActivityPanel>(p => p
        .Add(c => c.ConversationId, Guid.NewGuid())
        .Add(c => c.MessageId, Guid.NewGuid())
        .Add(c => c.Open, open)
        .Add(c => c.OpenChanged, EventCallback.Factory.Create<bool>(ctx, _ => { })));

    // AC-12
    [Test]
    public async Task Stored_closed_fetchesNothingUntilOpened_thenShowsEveryStep()
    {
        var (ctx, api) = NewContext();
        await using var _ = ctx;
        api.OnActivity = _ => ApiResult.Ok(Stored());
        var cut = RenderStored(ctx);

        await Assert.That(cut.Find(".activity-toggle").GetAttribute("aria-expanded")).IsEqualTo("false");
        await Assert.That(api.ActivityCalls.Count).IsEqualTo(0);
        await Assert.That(cut.FindAll(".activity-body").Count).IsEqualTo(0);

        cut.Find(".activity-toggle").Click();

        await Assert.That(api.ActivityCalls.Count).IsEqualTo(1);
        await Assert.That(cut.Find(".activity-toggle").GetAttribute("aria-expanded")).IsEqualTo("true");
        var titles = cut.FindAll(".activity-step-title").Select(e => e.TextContent).ToArray();
        await Assert.That(titles.SequenceEqual(["Question", "Rewrite", "Scope", "Retrieval", "Answer"])).IsTrue();
        var text = cut.Find(".activity-body").TextContent;
        await Assert.That(text).Contains("Who approves a budget exception?");
        await Assert.That(text).Contains("Enough evidence: 2 passages (minimum score 0.55, confident from 0.60) (12 ms)");
        await Assert.That(text).Contains("1. Budget Policy, Approval, page 3: score 0.91, cited");
        await Assert.That(text).Contains("2. Document unavailable: score 0.58");
        await Assert.That(text).Contains("Model called");
        await Assert.That(text).Contains("tags: finance");
    }

    // AC-6
    [Test]
    public async Task Stored_listsLeftOutDocuments()
    {
        var (ctx, api) = NewContext();
        await using var _ = ctx;
        api.OnActivity = _ => ApiResult.Ok(Stored());

        var cut = RenderStored(ctx, open: true);

        var left = cut.FindAll(".activity-scope-document").Single(e => e.GetAttribute("data-available") == "false");
        await Assert.That(left.TextContent).Contains("Old Policy");
        await Assert.That(left.TextContent).Contains("left out");
    }

    // AC-13
    [Test]
    public async Task Stored_refusal_saysTheModelWasNotCalled()
    {
        var (ctx, api) = NewContext();
        await using var _ = ctx;
        api.OnActivity = _ => ApiResult.Ok(Stored(modelCalled: false, status: "refused", refusal: "no_evidence"));

        var cut = RenderStored(ctx, open: true);

        var answer = cut.FindAll(".activity-step-text")[^1].TextContent;
        await Assert.That(answer).Contains("Model not called");
        await Assert.That(answer).Contains("refused");
        await Assert.That(answer).Contains("no evidence");
    }

    [Test]
    public async Task Stored_refusal_saysHowCloseTheBestPassageCame()
    {
        var (ctx, api) = NewContext();
        await using var disposeContext = ctx;
        var refused = Stored(modelCalled: false, status: "refused", refusal: "no_evidence");
        api.OnActivity = _ => ApiResult.Ok(refused with { Retrieval = new RetrievalStep("noEvidence", 0.55, 0.6, 8190, [], 0.48) });

        var cut = RenderStored(ctx, open: true);

        await Assert.That(cut.Find(".activity-body").TextContent)
            .Contains("No evidence: nothing reached the minimum score of 0.55; the closest passage scored 0.48 (8190 ms).");
    }

    [Test]
    public async Task Stored_failure_showsRetry_thatLoadsAgain()
    {
        var (ctx, api) = NewContext();
        await using var _ = ctx;
        var failing = true;
        api.OnActivity = _ => failing
            ? ApiResult.Fail<ActivityView>(new ApiError(0, "network", "down", new Dictionary<string, string[]>()))
            : ApiResult.Ok(Stored());
        var cut = RenderStored(ctx, open: true);
        await Assert.That(cut.Markup).Contains("The activity could not be loaded.");

        failing = false;
        cut.FindAll("fluent-button").First(b => b.TextContent.Trim() == "Retry").Click();

        await Assert.That(cut.FindAll(".activity-step-title").Count).IsEqualTo(5);
    }

    // AC-14
    [Test]
    public async Task Panel_neverShowsPassageTextPromptsOrTokens()
    {
        var (ctx, api) = NewContext();
        await using var _ = ctx;
        api.OnActivity = _ => ApiResult.Ok(Stored());

        var cut = RenderStored(ctx, open: true);

        var text = cut.Find(".activity-body").TextContent;
        await Assert.That(text.Contains("prompt", StringComparison.OrdinalIgnoreCase)).IsFalse();
        await Assert.That(text.Contains("tokens", StringComparison.OrdinalIgnoreCase)).IsFalse();
    }

    [Test]
    public async Task Stored_manyPassages_showTwentyThenAll()
    {
        var (ctx, api) = NewContext();
        await using var _ = ctx;
        var many = Stored() with
        {
            Retrieval = new RetrievalStep("sufficient", 0.5, 0.6, 5,
                [.. Enumerable.Range(1, 25).Select(i => new PassageStep(i, i, 0.9 - (i * 0.01), false, null, $"Doc {i}", 1, [], null, null))]),
        };
        api.OnActivity = _ => ApiResult.Ok(many);

        var cut = RenderStored(ctx, open: true);
        await Assert.That(cut.FindAll(".activity-passages li").Count).IsEqualTo(20);

        cut.FindAll("fluent-button").First(b => b.TextContent.Contains("Show all", StringComparison.Ordinal)).Click();

        await Assert.That(cut.FindAll(".activity-passages li").Count).IsEqualTo(25);
    }

    // AC-11: the panel stays open when the stored answer replaces the live one, and then shows the stored values
    [Test]
    public async Task Thread_keepsThePanelOpenWhenTheStoredAnswerReplacesTheLiveOne()
    {
        var (ctx, api) = NewContext();
        await using var _ = ctx;
        api.OnActivity = _ => ApiResult.Ok(Stored());
        using var live = new LiveAnswer(new ManualTimeProvider()) { Question = "Who approves a budget exception?" };
        var script = new ScriptedEvents();
        var assistantId = Guid.NewGuid();
        var run = live.ConsumeAsync(script.Events, CancellationToken.None);
        script.Send(new QuestionAccepted(Guid.NewGuid(), Guid.NewGuid(), assistantId));
        await ScriptedEvents.WaitUntilAsync(() => live.AssistantMessageId == assistantId);
        var cut = ctx.Render<ConversationThread>(p => p.Add(c => c.Live, live).Add(c => c.ConversationId, Guid.NewGuid()));

        cut.Find(".activity-toggle").Click();
        await Assert.That(cut.Find(".activity-toggle").GetAttribute("aria-expanded")).IsEqualTo("true");
        var stored = AnswerData.Assistant("complete", markers: 1) with { Id = assistantId };
        cut.Render(p => p.Add(c => c.Live, null).Add(c => c.Messages, [stored]).Add(c => c.ConversationId, Guid.NewGuid()));

        await Assert.That(cut.Find(".activity-toggle").GetAttribute("aria-expanded")).IsEqualTo("true");
        await Assert.That(api.ActivityCalls.Count).IsEqualTo(1);
        await Assert.That(cut.FindAll(".activity-step-title").Count).IsEqualTo(5);

        script.End();
        await run;
    }

    // AC-11
    [Test]
    public async Task Live_showsStepsAsTheyArrive_withoutFetching()
    {
        var (ctx, api) = NewContext();
        await using var _ = ctx;
        using var live = new LiveAnswer(new ManualTimeProvider()) { Question = "Who approves a budget exception above that amount?" };
        var script = new ScriptedEvents();
        var cut = ctx.Render<ActivityPanel>(p => p.Add(c => c.Live, live).Add(c => c.Open, true));
        var run = live.ConsumeAsync(script.Events, CancellationToken.None);
        var leftOut = Guid.NewGuid();

        script.Send(new QuestionAccepted(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
        script.Send(new RetrievalStarted("Who approves a budget exception above 10,000?"));
        script.Send(new RetrievalCompleted(
            "lowConfidence",
            [new AnswerSource(1, Guid.NewGuid(), Guid.NewGuid(), "Budget Policy", 1, ["Approval"], 2, 2, 0.63)],
            new ScopeInfo(null, [], [leftOut]), [leftOut], 0.55, 0.6, 8));
        await ScriptedEvents.WaitUntilAsync(() => live.ToActivity().Retrieval.Outcome == "lowConfidence");
        cut.Render();

        var text = cut.Find(".activity-body").TextContent;
        await Assert.That(text).Contains("Rewritten into “Who approves a budget exception above 10,000?”");
        await Assert.That(text).Contains("Low confidence: 1 passage");
        await Assert.That(text).Contains("1. Budget Policy, Approval, page 2: score 0.63");
        await Assert.That(text).Contains("Writing the answer…");
        await Assert.That(cut.FindAll(".activity-scope-document[data-available='false']").Count).IsEqualTo(1);
        await Assert.That(api.ActivityCalls.Count).IsEqualTo(0);

        script.End();
        await run;
    }
}
