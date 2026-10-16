using System.Threading.Channels;
using Bunit;
using Microsoft.AspNetCore.Components;
using RagAssistant.Web.Client.Chat;
using RagAssistant.Web.Tests.Documents;

namespace RagAssistant.Web.Tests.Chat;

/// <summary>An answer stream the test feeds event by event; completing it with an exception simulates a dropped connection.</summary>
internal sealed class ScriptedEvents
{
    private readonly Channel<AnswerEvent> _channel = Channel.CreateUnbounded<AnswerEvent>();

    public IAsyncEnumerable<AnswerEvent> Events => _channel.Reader.ReadAllAsync();

    public void Send(AnswerEvent item) => _channel.Writer.TryWrite(item);

    public void End(Exception? error = null) => _channel.Writer.TryComplete(error);

    public static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition was not met in time.");
            }

            await Task.Delay(5);
        }
    }
}

internal static class AnswerData
{
    public static ChatMessage Assistant(
        string status, string text = "An answer [1].", string? refusal = null, string? errorCode = null, bool? retryable = null, params int[] markers) =>
        new(Guid.NewGuid(), "assistant", 2, text, DateTimeOffset.UnixEpoch, Guid.NewGuid(), status, refusal, errorCode, retryable,
            DateTimeOffset.UnixEpoch.AddMinutes(1), [.. markers.Select(m => ChatTestData.Citation(m))]);

    public static AnswerSource Source(int marker) =>
        new(marker, Guid.NewGuid(), Guid.NewGuid(), $"Document {marker}", 1, ["Section"], null, null);

    public static RetrievalCompleted Retrieval(int count) =>
        new("sufficient", [.. Enumerable.Range(1, count).Select(Source)]);
}

// Spec 013, AC-4 to AC-8, AC-16, AC-17: the answer views and the live answer.
public class AnswerMessageTests
{
    private static BunitContext Context() => ChatTestData.NewContext(out _, out _);

    // AC-4
    [Test]
    public async Task Live_stream_showsStageThenTextThenFinalCitations()
    {
        await using var ctx = Context();
        var clock = new ManualTimeProvider();
        using var live = new LiveAnswer(clock);
        var script = new ScriptedEvents();
        var cut = ctx.Render<AnswerMessage>(p => p.Add(c => c.Live, live));
        var run = live.ConsumeAsync(script.Events, CancellationToken.None);

        script.Send(new QuestionAccepted(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
        script.Send(AnswerData.Retrieval(3));
        await ScriptedEvents.WaitUntilAsync(() => cut.Markup.Contains("Found 3 passages", StringComparison.Ordinal));
        await Assert.That(cut.FindAll("p").Count).IsEqualTo(0);

        script.Send(new AnswerDelta("Head [1"));
        await ScriptedEvents.WaitUntilAsync(() => live.ToView().Text == "Head [1");
        clock.Advance(LiveAnswer.RenderInterval);
        await ScriptedEvents.WaitUntilAsync(() => cut.Markup.Contains("Head [1", StringComparison.Ordinal));
        await Assert.That(cut.FindAll("button").Count).IsEqualTo(0);

        script.Send(new AnswerDelta("] and Finance [3]."));
        await ScriptedEvents.WaitUntilAsync(() => live.ToView().Text == "Head [1] and Finance [3].");
        clock.Advance(LiveAnswer.RenderInterval);
        await ScriptedEvents.WaitUntilAsync(() => cut.FindAll("button.citation-marker").Count == 2);

        script.Send(new AnswerCompleted("complete", null, [ChatTestData.Citation(1), ChatTestData.Citation(3)]));
        var outcome = await run;
        await ScriptedEvents.WaitUntilAsync(() => cut.FindAll("button.citation-item-main").Count == 2);

        await Assert.That(outcome).IsEqualTo(LiveOutcome.Finished);
        var names = cut.FindAll("button.citation-item-main").Select(b => b.GetAttribute("aria-label")!.Split(':')[0]).ToArray();
        await Assert.That(names).IsEquivalentTo(["Source 1", "Source 3"]);
    }

    // AC-17
    [Test]
    public async Task Live_stream_isBusyUntilFinal_andAnnouncesTheStagesInOrder()
    {
        await using var ctx = Context();
        using var live = new LiveAnswer(new ManualTimeProvider());
        var script = new ScriptedEvents();
        var cut = ctx.Render<AnswerMessage>(p => p.Add(c => c.Live, live));
        var run = live.ConsumeAsync(script.Events, CancellationToken.None);

        await Assert.That(cut.Find(".answer-message").GetAttribute("aria-busy")).IsEqualTo("true");
        script.Send(AnswerData.Retrieval(2));
        script.Send(new AnswerCompleted("complete", null, [ChatTestData.Citation(1), ChatTestData.Citation(2)]));
        await run;
        await ScriptedEvents.WaitUntilAsync(() => cut.Find(".answer-message").GetAttribute("aria-busy") == "false");

        await Assert.That(live.Announcements).IsEquivalentTo(["Searching the documents", "Writing the answer", "Answer complete, 2 sources"]);
    }

    // AC-16
    [Test]
    public async Task Live_manyDeltasWithinTheInterval_renderOnceByTheTimerAndOnceAtTheEnd()
    {
        await using var ctx = Context();
        var clock = new ManualTimeProvider();
        using var live = new LiveAnswer(clock);
        var script = new ScriptedEvents();
        var cut = ctx.Render<AnswerMessage>(p => p.Add(c => c.Live, live));
        var run = live.ConsumeAsync(script.Events, CancellationToken.None);
        script.Send(AnswerData.Retrieval(1));
        await ScriptedEvents.WaitUntilAsync(() => cut.Markup.Contains("Found 1 passage", StringComparison.Ordinal));
        var before = cut.RenderCount;

        for (var i = 0; i < 20; i++)
        {
            script.Send(new AnswerDelta("x"));
        }

        await ScriptedEvents.WaitUntilAsync(() => live.ToView().Text.Length == 20);
        clock.Advance(LiveAnswer.RenderInterval);
        await ScriptedEvents.WaitUntilAsync(() => cut.Markup.Contains(new string('x', 20), StringComparison.Ordinal));
        script.Send(new AnswerCompleted("partial", null, []));
        await run;
        await ScriptedEvents.WaitUntilAsync(() => cut.Markup.Contains("not linked to any source", StringComparison.Ordinal));

        await Assert.That(cut.RenderCount - before).IsLessThanOrEqualTo(2);
    }

    // AC-16
    [Test]
    public async Task Stored_message_isNotRenderedAgainByAParentRender()
    {
        await using var ctx = Context();
        var message = AnswerData.Assistant("complete", markers: 1);

        var cut = ctx.Render<AnswerMessage>(p => p.Add(c => c.Message, message));
        await Task.Delay(100);
        var before = cut.RenderCount;
        cut.Render(p => p.Add(c => c.Message, message));
        cut.Render(p => p.Add(c => c.Message, message));

        await Assert.That(cut.RenderCount).IsEqualTo(before);
    }

    // AC-8
    [Test]
    public async Task Live_streamEndingWithoutATerminalEvent_showsConnectionLost()
    {
        await using var ctx = Context();
        using var live = new LiveAnswer(new ManualTimeProvider());
        var script = new ScriptedEvents();
        var cut = ctx.Render<AnswerMessage>(p => p.Add(c => c.Live, live));
        var run = live.ConsumeAsync(script.Events, CancellationToken.None);

        script.Send(AnswerData.Retrieval(1));
        script.End();
        var outcome = await run;
        await ScriptedEvents.WaitUntilAsync(() => cut.Markup.Contains("Connection lost.", StringComparison.Ordinal));

        await Assert.That(outcome).IsEqualTo(LiveOutcome.Lost);
    }

    // AC-8
    [Test]
    public async Task Live_networkError_isLostNotAnException()
    {
        using var live = new LiveAnswer(new ManualTimeProvider());
        var script = new ScriptedEvents();
        var run = live.ConsumeAsync(script.Events, CancellationToken.None);

        script.End(new HttpRequestException("reset"));

        await Assert.That(await run).IsEqualTo(LiveOutcome.Lost);
    }

    // AC-7
    [Test]
    public async Task Live_cancelledToken_endsAsCancelled()
    {
        using var live = new LiveAnswer(new ManualTimeProvider());
        using var cts = new CancellationTokenSource();
        var script = new ScriptedEvents();
        var run = live.ConsumeAsync(script.Events, cts.Token);

        await cts.CancelAsync();

        await Assert.That(await run).IsEqualTo(LiveOutcome.Cancelled);
        await Assert.That(live.Announcements[^1]).IsEqualTo("Answer cancelled");
    }

    [Test]
    public async Task Live_errorEvent_showsTheMessageWithRetryWhenRetryable()
    {
        await using var ctx = Context();
        using var live = new LiveAnswer(new ManualTimeProvider());
        var script = new ScriptedEvents();
        var cut = ctx.Render<AnswerMessage>(p => p.Add(c => c.Live, live));
        var run = live.ConsumeAsync(script.Events, CancellationToken.None);

        script.Send(new AnswerError("chat.model_timeout", "The answer model took too long to respond. Try again.", true));
        await run;
        await ScriptedEvents.WaitUntilAsync(() => cut.FindAll("fluent-message-bar").Count == 1);

        await Assert.That(cut.Markup).Contains("took too long");
        await Assert.That(cut.FindAll("fluent-button").Select(b => b.TextContent.Trim())).IsEquivalentTo(["Retry"]);
        await Assert.That(live.Announcements[^1]).StartsWith("Error: ");
    }

    // AC-5
    [Test]
    public async Task Stored_partialWithCitations_warnsAndListsSources()
    {
        await using var ctx = Context();

        var cut = ctx.Render<AnswerMessage>(p => p.Add(c => c.Message, AnswerData.Assistant("partial", markers: 1)));

        await Assert.That(cut.Markup).Contains("Based on limited evidence. Check the sources.");
        await Assert.That(cut.FindAll(".citation-list").Count).IsEqualTo(1);
        await Assert.That(cut.FindAll("button.citation-marker").Count).IsEqualTo(1);
    }

    // AC-5
    [Test]
    public async Task Stored_partialWithoutCitations_warnsAndHasNoSourceList()
    {
        await using var ctx = Context();

        var cut = ctx.Render<AnswerMessage>(p => p.Add(c => c.Message, AnswerData.Assistant("partial", text: "Some text.")));

        await Assert.That(cut.Markup).Contains("This answer is not linked to any source.");
        await Assert.That(cut.Markup).Contains("Some text.");
        await Assert.That(cut.FindAll(".citation-list").Count).IsEqualTo(0);
    }

    // AC-5
    [Test]
    [Arguments("no_evidence", "The documents don't contain enough information to answer this.")]
    [Arguments("model_declined", "The passages found don't answer this question.")]
    public async Task Stored_refused_isAnInformationalCardWithoutRetry(string reason, string message)
    {
        await using var ctx = Context();

        var cut = ctx.Render<AnswerMessage>(p => p.Add(c => c.Message, AnswerData.Assistant("refused", text: "", refusal: reason)));

        await Assert.That(cut.Markup).Contains(message);
        await Assert.That(cut.FindAll("fluent-message-bar").Count).IsEqualTo(0);
        await Assert.That(cut.FindAll("fluent-button").Count).IsEqualTo(0);
    }

    // AC-5
    [Test]
    public async Task Stored_failedRetryable_isAnErrorBarWithRetry()
    {
        await using var ctx = Context();

        var cut = ctx.Render<AnswerMessage>(p => p.Add(c => c.Message,
            AnswerData.Assistant("failed", text: "", errorCode: "chat.model_unavailable", retryable: true)));

        await Assert.That(cut.Markup).Contains("The answer model is unavailable right now.");
        await Assert.That(cut.FindAll("fluent-message-bar").Count).IsEqualTo(1);
        await Assert.That(cut.FindAll("fluent-button").Select(b => b.TextContent.Trim())).IsEquivalentTo(["Retry"]);
    }

    // AC-5
    [Test]
    public async Task Stored_failedNotRetryable_offersAskAgainOnly()
    {
        await using var ctx = Context();
        var askedAgain = 0;

        var cut = ctx.Render<AnswerMessage>(p => p
            .Add(c => c.Message, AnswerData.Assistant("failed", text: "", errorCode: "chat.internal", retryable: false))
            .Add(c => c.OnAskAgain, EventCallback.Factory.Create(this, () => askedAgain++)));
        cut.Find("fluent-button").Click();

        await Assert.That(cut.FindAll("fluent-button").Select(b => b.TextContent.Trim())).IsEquivalentTo(["Ask again"]);
        await Assert.That(askedAgain).IsEqualTo(1);
    }

    // AC-5, AC-6
    [Test]
    public async Task Stored_cancelled_showsANeutralLineWithRetry()
    {
        await using var ctx = Context();
        var retried = 0;

        var cut = ctx.Render<AnswerMessage>(p => p
            .Add(c => c.Message, AnswerData.Assistant("cancelled", text: ""))
            .Add(c => c.OnRetry, EventCallback.Factory.Create(this, () => retried++)));
        cut.Find("fluent-button").Click();

        await Assert.That(cut.Markup).Contains("Answer cancelled.");
        await Assert.That(cut.FindAll("fluent-message-bar").Count).IsEqualTo(0);
        await Assert.That(retried).IsEqualTo(1);
    }

    // AC-14
    [Test]
    public async Task Stored_pending_showsTheNoticeWithRefresh()
    {
        await using var ctx = Context();
        var refreshed = 0;

        var cut = ctx.Render<AnswerMessage>(p => p
            .Add(c => c.Message, AnswerData.Assistant("pending", text: ""))
            .Add(c => c.OnRefresh, EventCallback.Factory.Create(this, () => refreshed++)));
        cut.Find("fluent-button").Click();

        await Assert.That(cut.Markup).Contains("An answer is being written in another tab or window.");
        await Assert.That(refreshed).IsEqualTo(1);
    }

    [Test]
    public async Task CitationClick_reportsTheMessageAndMarker()
    {
        await using var ctx = Context();
        var message = AnswerData.Assistant("complete", markers: 1);
        CitationSelection? chosen = null;

        var cut = ctx.Render<AnswerMessage>(p => p
            .Add(c => c.Message, message)
            .Add(c => c.OnCitationSelected, EventCallback.Factory.Create<CitationSelection>(this, s => chosen = s)));
        cut.Find("button.citation-item-main").Click();

        await Assert.That(chosen).IsEqualTo(new CitationSelection(message.Id, 1));
    }
}
