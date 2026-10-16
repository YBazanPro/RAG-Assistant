using Bunit;
using Microsoft.Extensions.DependencyInjection;
using RagAssistant.Web.Client.Chat;
using RagAssistant.Web.Client.Documents;
using ChatPage = RagAssistant.Web.Client.Pages.Chat;

namespace RagAssistant.Web.Tests.Chat;

// Spec 014, AC-15 and AC-18 to AC-19: the "Searched for" line and deleting conversations.
public class FollowUpUiTests
{
    private static ChatMessage UserMessage(string text, string? retrievalQuery) =>
        ChatPageHarness.Question(text) with { RetrievalQuery = retrievalQuery };

    private static AngleSharp.Dom.IElement DialogButton(IRenderedComponent<ChatPage> cut, string text) =>
        cut.FindAll("fluent-dialog fluent-button").First(b => b.TextContent.Trim() == text);

    // AC-15
    [Test]
    public async Task SearchedFor_isShownLiveWhenTheQueryDiffers_andAfterAReload()
    {
        await using var h = new ChatPageHarness();
        var id = Guid.NewGuid();
        var script = new ScriptedEvents();
        var question = UserMessage("Who approves it?", "Who approves a budget exception?");
        var answer = AnswerData.Assistant("complete", markers: 1) with { Sequence = 2, ReplyToMessageId = question.Id };
        var stored = false;
        h.Api.OnGet = i => ApiResult.Ok(stored ? ChatPageHarness.Detail(i, question, answer) : ChatPageHarness.Detail(i));
        h.Answers.OnAsk = _ => FakeAnswerEvents.Stream(script);
        var cut = h.Render(id);
        ChatPageHarness.WaitFor(cut, "Ask about 7 active documents.");

        await ChatPageHarness.TypeAsync(cut, "Who approves it?");
        _ = cut.InvokeAsync(() => h.EnterKey.OnEnter!(null));
        script.Send(new QuestionAccepted(id, question.Id, answer.Id));
        script.Send(new RetrievalStarted("Who approves a budget exception?"));
        ChatPageHarness.WaitFor(cut, "Searched for: Who approves a budget exception?");

        stored = true;
        script.Send(AnswerData.Retrieval(1));
        script.Send(new AnswerCompleted("complete", null, [ChatTestData.Citation(1)]));
        cut.WaitForState(() => cut.FindAll(".citation-list").Count == 1, TimeSpan.FromSeconds(5));

        await Assert.That(cut.FindAll(".searched-for").Count).IsEqualTo(1);
        await Assert.That(cut.Find(".searched-for").TextContent).IsEqualTo("Searched for: Who approves a budget exception?");
    }

    // AC-15
    [Test]
    public async Task SearchedFor_isNotShownWhenTheQueryEqualsTheQuestion()
    {
        await using var h = new ChatPageHarness();
        var id = Guid.NewGuid();
        var script = new ScriptedEvents();
        h.Api.OnGet = i => ApiResult.Ok(ChatPageHarness.Detail(i));
        h.Answers.OnAsk = _ => FakeAnswerEvents.Stream(script);
        var cut = h.Render(id);
        ChatPageHarness.WaitFor(cut, "Ask about 7 active documents.");

        await ChatPageHarness.TypeAsync(cut, "Who approves a budget exception?");
        _ = cut.InvokeAsync(() => h.EnterKey.OnEnter!(null));
        script.Send(new QuestionAccepted(id, Guid.NewGuid(), Guid.NewGuid()));
        script.Send(new RetrievalStarted("  Who approves a budget exception?  "));
        script.Send(AnswerData.Retrieval(1));
        ChatPageHarness.WaitFor(cut, "Found 1 passage");

        await Assert.That(cut.FindAll(".searched-for").Count).IsEqualTo(0);
        script.End();
    }

    // AC-15
    [Test]
    [Arguments("Who approves it?", "Who approves a budget exception?", true)]
    [Arguments("Who approves it?", "who approves it?", true)]
    [Arguments("Who approves it?", "Who approves it?", false)]
    [Arguments("Who approves it?", null, false)]
    public async Task SearchedFor_forStoredMessages_followsTheStoredRetrievalQuery(string text, string? query, bool shown)
    {
        await using var h = new ChatPageHarness();
        var question = UserMessage(text, query);
        var answer = AnswerData.Assistant("refused", text: "", refusal: "no_evidence") with { Sequence = 2, ReplyToMessageId = question.Id };
        h.Api.OnGet = i => ApiResult.Ok(ChatPageHarness.Detail(i, question, answer));

        var cut = h.Render(Guid.NewGuid());
        cut.WaitForState(() => cut.FindAll(".thread-question").Count == 1, TimeSpan.FromSeconds(5));

        await Assert.That(cut.FindAll(".searched-for").Count).IsEqualTo(shown ? 1 : 0);
    }

    // AC-18
    [Test]
    public async Task Delete_fromTheList_confirmsThenDeletesRefreshesAndClearsTheDraftAndKeepsTheOpenConversation()
    {
        await using var h = new ChatPageHarness();
        var all = new[] { Summary("First"), Summary("Second") };
        var deleted = new HashSet<Guid>();
        h.Api.OnList = page => ApiResult.Ok(new ConversationPage([.. all.Where(c => !deleted.Contains(c.Id))], page, 20, all.Length - deleted.Count));
        h.Api.OnGet = i => ApiResult.Ok(ChatPageHarness.Detail(i));
        h.Api.OnDelete = i =>
        {
            deleted.Add(i);
            return ApiResult.Ok(new Done());
        };
        await h.Drafts.SaveAsync($"ragassistant.chat.draft.{all[1].Id}", "half typed");
        var cut = h.Render(all[0].Id);
        cut.WaitForState(() => cut.FindAll(".conversation-row").Count == 2, TimeSpan.FromSeconds(5));
        var uri = h.Ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri;

        cut.FindAll(".conversation-delete")[1].Click();
        await Assert.That(cut.Markup).Contains("Delete this conversation?");
        DialogButton(cut, "Delete").Click();
        cut.WaitForState(() => cut.FindAll(".conversation-row").Count == 1, TimeSpan.FromSeconds(5));

        await Assert.That(h.Api.DeleteCalls).IsEquivalentTo([all[1].Id]);
        await Assert.That(h.Drafts.Drafts.ContainsKey($"ragassistant.chat.draft.{all[1].Id}")).IsFalse();
        await Assert.That(h.Ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri).IsEqualTo(uri);
    }

    // AC-18
    [Test]
    public async Task Delete_cancellingTheDialog_changesNothing()
    {
        await using var h = new ChatPageHarness();
        var item = Summary("Only");
        h.Api.OnList = page => ApiResult.Ok(new ConversationPage([item], page, 20, 1));
        h.Api.OnGet = i => ApiResult.Ok(ChatPageHarness.Detail(i));
        var cut = h.Render(item.Id);
        cut.WaitForState(() => cut.FindAll(".conversation-row").Count == 1, TimeSpan.FromSeconds(5));

        cut.Find(".conversation-delete").Click();
        DialogButton(cut, "Cancel").Click();
        cut.Find(".delete-conversation").Click();
        DialogButton(cut, "Cancel").Click();

        await Assert.That(h.Api.DeleteCalls.Count).IsEqualTo(0);
        await Assert.That(cut.FindAll(".conversation-row").Count).IsEqualTo(1);
    }

    // AC-18
    [Test]
    public async Task Delete_fromTheThreadHeader_navigatesToANewChatAndClearsTheDraft()
    {
        await using var h = new ChatPageHarness();
        var item = Summary("Open");
        h.Api.OnList = page => ApiResult.Ok(new ConversationPage([item], page, 20, 1));
        h.Api.OnGet = i => ApiResult.Ok(ChatPageHarness.Detail(i));
        await h.Drafts.SaveAsync($"ragassistant.chat.draft.{item.Id}", "unsent");
        var cut = h.Render(item.Id);
        cut.WaitForState(() => cut.FindAll(".delete-conversation").Count == 1, TimeSpan.FromSeconds(5));

        cut.Find(".delete-conversation").Click();
        DialogButton(cut, "Delete").Click();
        cut.WaitForState(() => h.Api.DeleteCalls.Count == 1, TimeSpan.FromSeconds(5));

        await Assert.That(h.Ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri).IsEqualTo("http://localhost/chat");
        await Assert.That(h.Drafts.Drafts.Count).IsEqualTo(0);
    }

    // AC-19
    [Test]
    public async Task Delete_whileThisTabStreams_cancelsTheStreamBeforeSendingDelete()
    {
        await using var h = new ChatPageHarness();
        var item = Summary("Streaming");
        var script = new ScriptedEvents();
        h.Api.OnList = page => ApiResult.Ok(new ConversationPage([item], page, 20, 1));
        h.Api.OnGet = i => ApiResult.Ok(ChatPageHarness.Detail(i));
        h.Answers.OnAsk = _ => FakeAnswerEvents.Stream(script);
        var cancelledWhenDeleted = false;
        h.Api.OnDelete = _ =>
        {
            cancelledWhenDeleted = h.Answers.LastAskToken.IsCancellationRequested;
            return ApiResult.Ok(new Done());
        };
        var cut = h.Render(item.Id);
        ChatPageHarness.WaitFor(cut, "Ask about 7 active documents.");
        await ChatPageHarness.TypeAsync(cut, "A question");
        _ = cut.InvokeAsync(() => h.EnterKey.OnEnter!(null));
        script.Send(new QuestionAccepted(item.Id, Guid.NewGuid(), Guid.NewGuid()));
        ChatPageHarness.WaitFor(cut, "Searching the documents");

        cut.Find(".delete-conversation").Click();
        DialogButton(cut, "Delete").Click();
        cut.WaitForState(() => h.Api.DeleteCalls.Count == 1, TimeSpan.FromSeconds(5));

        await Assert.That(cancelledWhenDeleted).IsTrue();
        script.End();
    }

    // AC-18: a conversation that is busy on the server stays, with the usual notice
    [Test]
    public async Task Delete_answerInProgressElsewhere_showsTheNotice()
    {
        await using var h = new ChatPageHarness();
        var item = Summary("Busy");
        h.Api.OnList = page => ApiResult.Ok(new ConversationPage([item], page, 20, 1));
        h.Api.OnGet = i => ApiResult.Ok(ChatPageHarness.Detail(i));
        h.Api.OnDelete = _ => ApiResult.Fail<Done>(new ApiError(409, "chat.answer_in_progress", "busy", new Dictionary<string, string[]>()));
        var cut = h.Render(item.Id);
        cut.WaitForState(() => cut.FindAll(".delete-conversation").Count == 1, TimeSpan.FromSeconds(5));

        cut.Find(".delete-conversation").Click();
        DialogButton(cut, "Delete").Click();
        ChatPageHarness.WaitFor(cut, "An answer is being written in another tab or window.");

        await Assert.That(cut.FindAll(".conversation-row").Count).IsEqualTo(1);
    }

    private static ConversationSummary Summary(string title) =>
        new(Guid.NewGuid(), title, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
}
