using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using RagAssistant.Web.Client.Chat;

namespace RagAssistant.Web.Tests.Chat;

// Spec 013, AC-3 (Enter handling itself is browser code, covered by the smoke tests) and the thread's scrolling.
public class QuestionInputTests
{
    private static bool IsDisabled(IRenderedComponent<QuestionInput> cut, string label) =>
        cut.FindAll("fluent-button").First(b => b.TextContent.Trim() == label).HasAttribute("disabled");

    [Test]
    [Arguments("", true)]
    [Arguments("   \n ", true)]
    [Arguments("What is the limit?", false)]
    public async Task Send_isDisabledForEmptyOrWhitespaceText(string text, bool disabled)
    {
        await using var ctx = ChatTestData.NewContext(out _, out _);

        var cut = ctx.Render<QuestionInput>(p => p.Add(c => c.Text, text));

        await Assert.That(IsDisabled(cut, "Send")).IsEqualTo(disabled);
    }

    [Test]
    public async Task Send_isDisabledAboveOneThousandCharactersAfterTrimming()
    {
        await using var ctx = ChatTestData.NewContext(out _, out _);

        var atLimit = ctx.Render<QuestionInput>(p => p.Add(c => c.Text, "  " + new string('a', 1000) + "  "));
        var over = ctx.Render<QuestionInput>(p => p.Add(c => c.Text, new string('a', 1001)));

        await Assert.That(IsDisabled(atLimit, "Send")).IsFalse();
        await Assert.That(IsDisabled(over, "Send")).IsTrue();
        await Assert.That(over.Markup).Contains("at most 1,000 characters");
    }

    [Test]
    public async Task Counter_appearsPastNineHundredCharacters()
    {
        await using var ctx = ChatTestData.NewContext(out _, out _);

        var atNineHundred = ctx.Render<QuestionInput>(p => p.Add(c => c.Text, new string('a', 900)));
        var past = ctx.Render<QuestionInput>(p => p.Add(c => c.Text, new string('a', 901)));

        await Assert.That(atNineHundred.FindAll(".question-counter").Count).IsEqualTo(0);
        await Assert.That(past.Find(".question-counter").TextContent.Trim()).IsEqualTo("901 / 1000");
    }

    [Test]
    public async Task Send_sendsTheTrimmedText()
    {
        await using var ctx = ChatTestData.NewContext(out _, out _);
        string? sent = null;

        var cut = ctx.Render<QuestionInput>(p => p
            .Add(c => c.Text, "  Where is the policy?  ")
            .Add(c => c.OnSend, EventCallback.Factory.Create<string>(this, t => sent = t)));
        cut.FindAll("fluent-button").First(b => b.TextContent.Trim() == "Send").Click();

        await Assert.That(sent).IsEqualTo("Where is the policy?");
    }

    [Test]
    public async Task Enter_sendsOnceAndIsIgnoredWhileSendingOrStreaming()
    {
        await using var ctx = ChatTestData.NewContext(out _, out _);
        var enter = (FakeEnterKey)ctx.Services.GetRequiredService<IEnterKeyHandler>();
        var sent = 0;

        var cut = ctx.Render<QuestionInput>(p => p
            .Add(c => c.Text, "Question")
            .Add(c => c.OnSend, EventCallback.Factory.Create<string>(this, _ => sent++)));
        await cut.InvokeAsync(() => enter.OnEnter!(null));
        cut.Render(p => p.Add(c => c.Sending, true));
        await cut.InvokeAsync(() => enter.OnEnter!(null));
        cut.Render(p => p.Add(c => c.Sending, false).Add(c => c.Streaming, true));
        await cut.InvokeAsync(() => enter.OnEnter!(null));

        await Assert.That(sent).IsEqualTo(1);
    }

    // The value change is debounced in the browser, so a fast Enter carries the textarea's current value.
    [Test]
    public async Task Enter_usesTheValueTheBrowserSendsAlong()
    {
        await using var ctx = ChatTestData.NewContext(out _, out _);
        var enter = (FakeEnterKey)ctx.Services.GetRequiredService<IEnterKeyHandler>();
        string? sent = null;

        var cut = ctx.Render<QuestionInput>(p => p
            .Add(c => c.Text, "old")
            .Add(c => c.OnSend, EventCallback.Factory.Create<string>(this, t => sent = t)));
        await cut.InvokeAsync(() => enter.OnEnter!("typed just now"));

        await Assert.That(sent).IsEqualTo("typed just now");
    }

    // AC-7
    [Test]
    public async Task Streaming_sendBecomesCancelAndTheTextStaysEditable()
    {
        await using var ctx = ChatTestData.NewContext(out _, out _);
        var cancelled = 0;
        string? edited = null;

        var cut = ctx.Render<QuestionInput>(p => p
            .Add(c => c.Streaming, true)
            .Add(c => c.OnCancel, EventCallback.Factory.Create(this, () => cancelled++))
            .Add(c => c.TextChanged, EventCallback.Factory.Create<string>(this, t => edited = t)));
        await cut.InvokeAsync(() => cut.FindComponent<FluentTextArea>().Instance.ValueChanged.InvokeAsync("next question"));
        cut.FindAll("fluent-button").First(b => b.TextContent.Trim() == "Cancel").Click();

        await Assert.That(cut.FindAll("fluent-button").Select(b => b.TextContent.Trim())).IsEquivalentTo(["Cancel"]);
        await Assert.That(cut.FindComponent<FluentTextArea>().Instance.Disabled).IsFalse();
        await Assert.That(edited).IsEqualTo("next question");
        await Assert.That(cancelled).IsEqualTo(1);
    }

    [Test]
    public async Task Thread_showsEachQuestionAboveItsAnswer()
    {
        await using var ctx = ChatTestData.NewContext(out _, out _);
        var question = new ChatMessage(Guid.NewGuid(), "user", 1, "What is the limit?", DateTimeOffset.UnixEpoch, null, null, null, null, null, null, null);
        var answer = AnswerData.Assistant("refused", text: "", refusal: "no_evidence") with { Sequence = 2, ReplyToMessageId = question.Id };

        var cut = ctx.Render<ConversationThread>(p => p.Add(c => c.Messages, [question, answer]));
        var order = cut.FindAll(".thread-question, .thread-answer").Select(e => e.ClassList.Contains("thread-question") ? "question" : "answer").ToArray();

        await Assert.That(order).IsEquivalentTo(["question", "answer"]);
        await Assert.That(cut.Find(".thread-question-text").TextContent).IsEqualTo("What is the limit?");
    }

    // AC-16
    [Test]
    public async Task Thread_labelsEachRoleForScreenReadersAndStylesThemApart()
    {
        await using var ctx = ChatTestData.NewContext(out _, out _);
        var question = new ChatMessage(Guid.NewGuid(), "user", 1, "What is the limit?", DateTimeOffset.UnixEpoch, null, null, null, null, null, null, null);
        var answer = AnswerData.Assistant("refused", text: "", refusal: "no_evidence") with { Sequence = 2, ReplyToMessageId = question.Id };

        var cut = ctx.Render<ConversationThread>(p => p.Add(c => c.Messages, [question, answer]));

        await Assert.That(cut.Find(".thread-question .visually-hidden").TextContent.Trim()).IsEqualTo("You said");
        await Assert.That(cut.Find(".thread-answer .visually-hidden").TextContent.Trim()).IsEqualTo("Assistant said");
        await Assert.That(cut.FindAll(".thread-question .thread-bubble").Count).IsEqualTo(1);
        await Assert.That(cut.FindAll(".thread-answer .assistant-label").Count).IsEqualTo(1);
    }

    [Test]
    public async Task Thread_showsThePendingQuestionBeforeAnyEvent()
    {
        await using var ctx = ChatTestData.NewContext(out _, out _);

        var cut = ctx.Render<ConversationThread>(p => p.Add(c => c.PendingQuestion, "Is it allowed?"));

        await Assert.That(cut.Find(".thread-question-text").TextContent).IsEqualTo("Is it allowed?");
    }

    [Test]
    public async Task Thread_jumpToLatest_appearsWhenTheReaderScrollsAwayAndGoesWhenTheyReturn()
    {
        await using var ctx = ChatTestData.NewContext(out _, out var scroller);
        var cut = ctx.Render<ConversationThread>();

        await Assert.That(cut.FindAll(".jump-to-latest").Count).IsEqualTo(0);
        await cut.InvokeAsync(() => scroller.ReportFollowingAsync(false));
        await Assert.That(cut.FindAll(".jump-to-latest").Count).IsEqualTo(1);

        cut.Find(".jump-to-latest").Click();
        await Assert.That(scroller.Scrolls[^1]).IsTrue();
        await cut.InvokeAsync(() => scroller.ReportFollowingAsync(true));
        await Assert.That(cut.FindAll(".jump-to-latest").Count).IsEqualTo(0);
    }

    [Test]
    public async Task Thread_openingAConversation_scrollsToItsLastMessage()
    {
        await using var ctx = ChatTestData.NewContext(out _, out var scroller);
        var question = new ChatMessage(Guid.NewGuid(), "user", 1, "Q", DateTimeOffset.UnixEpoch, null, null, null, null, null, null, null);

        ctx.Render<ConversationThread>(p => p.Add(c => c.Messages, [question]).Add(c => c.ConversationId, Guid.NewGuid()));

        await Assert.That(scroller.Scrolls).Contains(true);
    }

    // AC-17
    [Test]
    public async Task Thread_announcementGoesIntoTheLiveRegion()
    {
        await using var ctx = ChatTestData.NewContext(out _, out _);

        var cut = ctx.Render<ConversationThread>(p => p.Add(c => c.Announcement, "Writing the answer"));
        var region = cut.Find("[aria-live=polite]");

        await Assert.That(region.TextContent).IsEqualTo("Writing the answer");
        await Assert.That(region.ClassList).Contains("visually-hidden");
    }
}
