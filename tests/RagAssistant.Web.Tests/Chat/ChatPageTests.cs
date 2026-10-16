using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using RagAssistant.Security;
using RagAssistant.Web.Client.Chat;
using RagAssistant.Web.Client.Documents;
using RagAssistant.Web.Tests.Documents;
using ChatPage = RagAssistant.Web.Client.Pages.Chat;

namespace RagAssistant.Web.Tests.Chat;

public sealed class FakeChatApi : IChatApi
{
    public Func<ApiResult<ConversationSummary>> OnCreate { get; set; } = () =>
        ApiResult.Ok(new ConversationSummary(Guid.NewGuid(), null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));

    public Func<int, ApiResult<ConversationPage>> OnList { get; set; } = page =>
        ApiResult.Ok(new ConversationPage([], page, 20, 0));

    public List<Guid> DeleteCalls { get; } = [];

    public List<(Guid Message, string Rating)> RatingCalls { get; } = [];

    public List<Guid> ClearCalls { get; } = [];

    public List<(Guid Message, string Category, string? Comment)> ReportCalls { get; } = [];

    public Func<Guid, ApiResult<Done>> OnDelete { get; set; } = _ => ApiResult.Ok(new Done());

    public Func<string, ApiResult<RatingResult>> OnRate { get; set; } = rating => ApiResult.Ok(new RatingResult(rating, DateTimeOffset.UnixEpoch));

    public Func<ApiResult<Done>> OnClear { get; set; } = () => ApiResult.Ok(new Done());

    public Func<string, ApiResult<IssueReportResult>> OnReport { get; set; } = category =>
        ApiResult.Ok(new IssueReportResult(Guid.NewGuid(), category, null, DateTimeOffset.UnixEpoch));

    public Func<Guid, ApiResult<ConversationDetail>> OnGet { get; set; } = id =>
        ApiResult.Ok(new ConversationDetail(id, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, []));

    public Func<Guid, ApiResult<ActivityView>> OnActivity { get; set; } = _ =>
        ApiResult.Fail<ActivityView>(new ApiError(404, "chat.activity_not_found", "There is no activity for this answer.", new Dictionary<string, string[]>()));

    public List<Guid> ActivityCalls { get; } = [];

    public List<(Guid Conversation, Guid[] Documents, string[] Tags)> ScopeCalls { get; } = [];

    public Func<Guid[], string[], ApiResult<ConversationScope>> OnScope { get; set; } = (documents, tags) =>
        ApiResult.Ok(new ConversationScope(documents, tags, [], [.. documents.Select(id => new ScopeDocument(id, $"Document {id.ToString()[..4]}", true))]));

    public int CreateCalls { get; private set; }

    public List<Guid> GetCalls { get; } = [];

    public List<int> ListCalls { get; } = [];

    public Task<ApiResult<ConversationSummary>> CreateConversationAsync(CancellationToken cancellationToken)
    {
        CreateCalls++;
        return Task.FromResult(OnCreate());
    }

    public Task<ApiResult<ConversationPage>> ListConversationsAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        ListCalls.Add(page);
        return Task.FromResult(OnList(page));
    }

    public Task<ApiResult<Done>> DeleteConversationAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        DeleteCalls.Add(conversationId);
        return Task.FromResult(OnDelete(conversationId));
    }

    public Task<ApiResult<RatingResult>> SetRatingAsync(Guid conversationId, Guid messageId, string rating, CancellationToken cancellationToken)
    {
        RatingCalls.Add((messageId, rating));
        return Task.FromResult(OnRate(rating));
    }

    public Task<ApiResult<Done>> ClearRatingAsync(Guid conversationId, Guid messageId, CancellationToken cancellationToken)
    {
        ClearCalls.Add(messageId);
        return Task.FromResult(OnClear());
    }

    public Task<ApiResult<IssueReportResult>> ReportIssueAsync(
        Guid conversationId, Guid messageId, string category, string? comment, CancellationToken cancellationToken)
    {
        ReportCalls.Add((messageId, category, comment));
        return Task.FromResult(OnReport(category));
    }

    public Task<ApiResult<ConversationScope>> SetScopeAsync(
        Guid conversationId, IReadOnlyList<Guid> documentIds, IReadOnlyList<string> tags, CancellationToken cancellationToken)
    {
        ScopeCalls.Add((conversationId, [.. documentIds], [.. tags]));
        return Task.FromResult(OnScope([.. documentIds], [.. tags]));
    }

    public Task<ApiResult<ActivityView>> GetActivityAsync(Guid conversationId, Guid messageId, CancellationToken cancellationToken)
    {
        ActivityCalls.Add(messageId);
        return Task.FromResult(OnActivity(messageId));
    }

    public Task<ApiResult<ConversationDetail>> GetConversationAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        GetCalls.Add(conversationId);
        return Task.FromResult(OnGet(conversationId));
    }
}

internal sealed class FakeAnswerEvents : IAnswerEventSource
{
    public Func<string, ApiResult<IAsyncEnumerable<AnswerEvent>>> OnAsk { get; set; } = _ => throw new InvalidOperationException("No stream scripted.");

    public Func<Guid, ApiResult<IAsyncEnumerable<AnswerEvent>>> OnRetry { get; set; } = _ => throw new InvalidOperationException("No stream scripted.");

    public List<(Guid ConversationId, string Question)> AskCalls { get; } = [];

    public List<(Guid ConversationId, Guid MessageId)> RetryCalls { get; } = [];

    /// <summary>The token of the latest ask, so a test can see whether the page cancelled the stream.</summary>
    public CancellationToken LastAskToken { get; private set; }

    public Task<ApiResult<IAsyncEnumerable<AnswerEvent>>> AskAsync(Guid conversationId, string question, CancellationToken cancellationToken)
    {
        LastAskToken = cancellationToken;
        AskCalls.Add((conversationId, question));
        return Task.FromResult(OnAsk(question));
    }

    public Task<ApiResult<IAsyncEnumerable<AnswerEvent>>> RetryAsync(Guid conversationId, Guid assistantMessageId, CancellationToken cancellationToken)
    {
        RetryCalls.Add((conversationId, assistantMessageId));
        return Task.FromResult(OnRetry(assistantMessageId));
    }

    public static ApiResult<IAsyncEnumerable<AnswerEvent>> Stream(ScriptedEvents script) => ApiResult.Ok(script.Events);

    public static ApiResult<IAsyncEnumerable<AnswerEvent>> Failure(int status, string code = "x", string message = "failed") =>
        ApiResult.Fail<IAsyncEnumerable<AnswerEvent>>(new ApiError(status, code, message, new Dictionary<string, string[]>()));
}

internal sealed class ChatPageHarness : IAsyncDisposable
{
    public BunitContext Ctx { get; }

    public FakeChatApi Api { get; } = new();

    public FakeAnswerEvents Answers { get; } = new();

    public FakeDraftStore Drafts { get; } = new();

    public FakeViewport Viewport { get; } = new();

    public FakeDocumentsApi Documents { get; } = new();

    public FakeEnterKey EnterKey { get; }

    public ManualTimeProvider Clock { get; } = new();

    public FakeThreadScroller Scroller { get; }

    public ChatPageHarness()
    {
        Ctx = ChatTestData.NewContext(out _, out var scroller);
        Scroller = scroller;
        Ctx.Services.AddSingleton<IChatApi>(Api);
        Ctx.Services.AddSingleton<IAnswerEventSource>(Answers);
        Ctx.Services.AddSingleton<IDocumentsApi>(Documents);
        Ctx.Services.AddSingleton<IDraftStore>(Drafts);
        Ctx.Services.AddSingleton<IViewport>(Viewport);
        Ctx.Services.AddSingleton<TimeProvider>(Clock);
        Ctx.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Chat:SuggestedQuestions:0"] = "What is the approval limit?",
            ["Chat:SuggestedQuestions:1"] = "Who signs off exceptions?",
        }).Build());
        Ctx.AddAuthorization().SetAuthorized("someone").SetPolicies([PolicyNames.KnowledgeSeeker]);
        EnterKey = (FakeEnterKey)Ctx.Services.GetRequiredService<IEnterKeyHandler>();
        Documents.OnList = _ => ApiResult.Ok(new PagedResult<DocumentSummary>([], 1, 20, 7));
    }

    public IRenderedComponent<ChatPage> Render(Guid? conversationId = null)
    {
        Ctx.Services.GetRequiredService<NavigationManager>().NavigateTo(conversationId is null ? "http://localhost/chat" : $"http://localhost/chat/{conversationId}");
        return Ctx.Render<ChatPage>(p => p.Add(c => c.ConversationId, conversationId));
    }

    public static void WaitFor(IRenderedComponent<ChatPage> cut, string text) =>
        cut.WaitForState(() => cut.Markup.Contains(text, StringComparison.Ordinal), TimeSpan.FromSeconds(5));

    public static async Task TypeAsync(IRenderedComponent<ChatPage> cut, string text) =>
        await cut.InvokeAsync(() => cut.FindComponent<FluentTextArea>().Instance.ValueChanged.InvokeAsync(text));

    public ValueTask DisposeAsync() => Ctx.DisposeAsync();

    public static ConversationDetail Detail(Guid id, params ChatMessage[] messages) =>
        new(id, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, messages);

    public static ChatMessage Question(string text, int sequence = 1) =>
        new(Guid.NewGuid(), "user", sequence, text, DateTimeOffset.UnixEpoch, null, null, null, null, null, null, null);
}

// Spec 013, AC-1, AC-2, AC-6 to AC-8, AC-12 to AC-15.
public class ChatPageTests
{
    // AC-1
    [Test]
    public async Task NewChat_showsTheEmptyStateAndCreatesNothing()
    {
        await using var h = new ChatPageHarness();

        var cut = h.Render();
        ChatPageHarness.WaitFor(cut, "Ask about 7 active documents.");

        await Assert.That(h.Api.CreateCalls).IsEqualTo(0);
        var suggestions = cut.FindAll(".suggested-questions fluent-button").Select(b => b.TextContent.Trim()).ToArray();
        await Assert.That(suggestions).IsEquivalentTo(["What is the approval limit?", "Who signs off exceptions?"]);

        cut.FindAll(".suggested-questions fluent-button")[0].Click();
        await Assert.That(cut.FindComponent<QuestionInput>().Instance.Text).IsEqualTo("What is the approval limit?");
        await Assert.That(h.Answers.AskCalls.Count).IsEqualTo(0);
    }

    // AC-1
    [Test]
    public async Task NewChat_withNoDocuments_saysSoAndLinksToDocuments()
    {
        await using var h = new ChatPageHarness();
        h.Documents.OnList = _ => ApiResult.Ok(new PagedResult<DocumentSummary>([], 1, 20, 0));

        var cut = h.Render();
        ChatPageHarness.WaitFor(cut, "No documents are indexed yet.");

        await Assert.That(cut.Find(".chat-empty a").GetAttribute("href")).IsEqualTo("documents");
    }

    // AC-2
    [Test]
    public async Task FirstSend_createsTheConversationAsksAndReplacesTheUrl()
    {
        await using var h = new ChatPageHarness();
        var id = Guid.NewGuid();
        var script = new ScriptedEvents();
        h.Api.OnCreate = () => ApiResult.Ok(new ConversationSummary(id, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));
        h.Answers.OnAsk = _ => FakeAnswerEvents.Stream(script);
        var cut = h.Render();
        ChatPageHarness.WaitFor(cut, "Ask about 7 active documents.");

        await ChatPageHarness.TypeAsync(cut, "What is the limit?");
        _ = cut.InvokeAsync(() => h.EnterKey.OnEnter!(null));
        ChatPageHarness.WaitFor(cut, "Searching the documents");

        await Assert.That(h.Api.CreateCalls).IsEqualTo(1);
        await Assert.That(h.Answers.AskCalls).IsEquivalentTo([(id, "What is the limit?")]);
        await Assert.That(h.Ctx.Services.GetRequiredService<NavigationManager>().Uri).IsEqualTo($"http://localhost/chat/{id}");
        await Assert.That(cut.Find(".thread-question-text").TextContent).IsEqualTo("What is the limit?");
        await Assert.That(cut.FindComponent<QuestionInput>().Instance.Text).IsEqualTo("");
        await Assert.That(cut.FindAll(".question-actions fluent-button").Any(b => b.TextContent.Trim() == "Cancel")).IsTrue();
        script.End();
    }

    // AC-2: a second Send while the first is in flight asks only once
    [Test]
    public async Task Send_whileBusy_isIgnored()
    {
        await using var h = new ChatPageHarness();
        var id = Guid.NewGuid();
        var script = new ScriptedEvents();
        h.Api.OnGet = i => ApiResult.Ok(ChatPageHarness.Detail(i));
        h.Answers.OnAsk = _ => FakeAnswerEvents.Stream(script);
        var cut = h.Render(id);
        ChatPageHarness.WaitFor(cut, "Ask about 7 active documents.");

        await ChatPageHarness.TypeAsync(cut, "First");
        _ = cut.InvokeAsync(() => h.EnterKey.OnEnter!(null));
        await ChatPageHarness.TypeAsync(cut, "Second");
        _ = cut.InvokeAsync(() => h.EnterKey.OnEnter!(null));

        await Assert.That(h.Answers.AskCalls.Count).IsEqualTo(1);
        script.End();
    }

    // AC-4, AC-10
    [Test]
    public async Task Answer_completes_thenTheConversationIsRefetchedAndACitationOpensThePreview()
    {
        await using var h = new ChatPageHarness();
        var id = Guid.NewGuid();
        var script = new ScriptedEvents();
        var question = ChatPageHarness.Question("What is the limit?");
        var answer = AnswerData.Assistant("complete", text: "It is 5000 [1].", markers: 1) with { Sequence = 2, ReplyToMessageId = question.Id };
        var stored = false;
        h.Api.OnGet = i => ApiResult.Ok(stored ? ChatPageHarness.Detail(i, question, answer) : ChatPageHarness.Detail(i));
        h.Answers.OnAsk = _ => FakeAnswerEvents.Stream(script);
        var cut = h.Render(id);
        ChatPageHarness.WaitFor(cut, "Ask about 7 active documents.");

        await ChatPageHarness.TypeAsync(cut, "What is the limit?");
        _ = cut.InvokeAsync(() => h.EnterKey.OnEnter!(null));
        script.Send(new QuestionAccepted(id, question.Id, answer.Id));
        script.Send(AnswerData.Retrieval(1));
        script.Send(new AnswerDelta("It is 5000 [1]."));
        stored = true;
        script.Send(new AnswerCompleted("complete", null, [ChatTestData.Citation(1)]));
        ChatPageHarness.WaitFor(cut, "Sources");
        cut.WaitForState(() => cut.FindAll(".question-actions fluent-button").All(b => b.TextContent.Trim() != "Cancel"), TimeSpan.FromSeconds(5));

        await Assert.That(h.Api.GetCalls.Count).IsEqualTo(2);
        await Assert.That(h.Api.ListCalls.Count).IsGreaterThanOrEqualTo(2);

        cut.Find("button.citation-marker").Click();
        await Assert.That(cut.FindAll("aside.source-preview").Count).IsEqualTo(1);
        await Assert.That(cut.Find("aside.source-preview pre").TextContent).IsEqualTo("Exceptions above 5000 need approval.");
        cut.Find("aside.source-preview").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });
        await Assert.That(cut.FindAll("aside.source-preview").Count).IsEqualTo(0);
        await Assert.That(h.Scroller.Focused).IsEquivalentTo([$"m{answer.Id:N}-marker-1"]);
    }

    // AC-7
    [Test]
    public async Task Cancel_showsAnswerCancelledWithRetryAfterTheRefetch()
    {
        await using var h = new ChatPageHarness();
        var id = Guid.NewGuid();
        var script = new ScriptedEvents();
        var question = ChatPageHarness.Question("Slow question");
        var cancelled = AnswerData.Assistant("cancelled", text: "") with { Sequence = 2, ReplyToMessageId = question.Id };
        var accepted = false;
        h.Api.OnGet = i => ApiResult.Ok(accepted ? ChatPageHarness.Detail(i, question, cancelled) : ChatPageHarness.Detail(i));
        h.Answers.OnAsk = _ => FakeAnswerEvents.Stream(script);
        var cut = h.Render(id);
        ChatPageHarness.WaitFor(cut, "Ask about 7 active documents.");

        await ChatPageHarness.TypeAsync(cut, "Slow question");
        _ = cut.InvokeAsync(() => h.EnterKey.OnEnter!(null));
        script.Send(new QuestionAccepted(id, question.Id, cancelled.Id));
        ChatPageHarness.WaitFor(cut, "Cancel");
        accepted = true;
        cut.FindAll(".question-actions fluent-button").First(b => b.TextContent.Trim() == "Cancel").Click();
        ChatPageHarness.WaitFor(cut, "Answer cancelled.");

        await Assert.That(cut.FindAll("fluent-button").Any(b => b.TextContent.Trim() == "Retry")).IsTrue();
    }

    // AC-7: a question the server never accepted goes back into the input
    [Test]
    public async Task Cancel_beforeTheQuestionIsAccepted_restoresItToTheInput()
    {
        await using var h = new ChatPageHarness();
        var id = Guid.NewGuid();
        var script = new ScriptedEvents();
        h.Api.OnGet = i => ApiResult.Ok(ChatPageHarness.Detail(i));
        h.Answers.OnAsk = _ => FakeAnswerEvents.Stream(script);
        var cut = h.Render(id);
        ChatPageHarness.WaitFor(cut, "Ask about 7 active documents.");

        await ChatPageHarness.TypeAsync(cut, "Never accepted");
        _ = cut.InvokeAsync(() => h.EnterKey.OnEnter!(null));
        ChatPageHarness.WaitFor(cut, "Cancel");
        cut.FindAll(".question-actions fluent-button").First(b => b.TextContent.Trim() == "Cancel").Click();
        cut.WaitForState(() => cut.FindComponent<QuestionInput>().Instance.Text == "Never accepted", TimeSpan.FromSeconds(5));

        await Assert.That(cut.FindAll(".thread-question").Count).IsEqualTo(0);
    }

    // AC-6
    [Test]
    public async Task Retry_callsTheRetryEndpointForThatMessageAndStreamsTheNewAnswer()
    {
        await using var h = new ChatPageHarness();
        var id = Guid.NewGuid();
        var script = new ScriptedEvents();
        var question = ChatPageHarness.Question("Q");
        var failed = AnswerData.Assistant("failed", text: "", errorCode: "chat.model_unavailable", retryable: true) with { Sequence = 2, ReplyToMessageId = question.Id };
        h.Api.OnGet = i => ApiResult.Ok(ChatPageHarness.Detail(i, question, failed));
        h.Answers.OnRetry = _ => FakeAnswerEvents.Stream(script);
        var cut = h.Render(id);
        ChatPageHarness.WaitFor(cut, "The answer model is unavailable");

        cut.FindAll("fluent-message-bar fluent-button").First(b => b.TextContent.Trim() == "Retry").Click();
        ChatPageHarness.WaitFor(cut, "Searching the documents");

        await Assert.That(h.Answers.RetryCalls).IsEquivalentTo([(id, failed.Id)]);
        await Assert.That(cut.Markup).DoesNotContain("The answer model is unavailable");
        script.End();
    }

    // AC-8
    [Test]
    public async Task ConnectionLost_refetchesOnceAndShowsTheServerState()
    {
        await using var h = new ChatPageHarness();
        var id = Guid.NewGuid();
        var script = new ScriptedEvents();
        var question = ChatPageHarness.Question("Q");
        var cancelled = AnswerData.Assistant("cancelled", text: "") with { Sequence = 2, ReplyToMessageId = question.Id };
        var dropped = false;
        h.Api.OnGet = i => ApiResult.Ok(dropped ? ChatPageHarness.Detail(i, question, cancelled) : ChatPageHarness.Detail(i));
        h.Answers.OnAsk = _ => FakeAnswerEvents.Stream(script);
        var cut = h.Render(id);
        ChatPageHarness.WaitFor(cut, "Ask about 7 active documents.");

        await ChatPageHarness.TypeAsync(cut, "Q");
        _ = cut.InvokeAsync(() => h.EnterKey.OnEnter!(null));
        script.Send(new QuestionAccepted(id, question.Id, cancelled.Id));
        dropped = true;
        script.End();
        ChatPageHarness.WaitFor(cut, "Answer cancelled.");

        await Assert.That(h.Api.GetCalls.Count).IsEqualTo(2);
    }

    // AC-12
    [Test]
    public async Task Send_unauthenticated_showsSignInAndKeepsTheDraft()
    {
        await using var h = new ChatPageHarness();
        var id = Guid.NewGuid();
        h.Api.OnGet = i => ApiResult.Ok(ChatPageHarness.Detail(i));
        h.Answers.OnAsk = _ => FakeAnswerEvents.Failure(401);
        var cut = h.Render(id);
        ChatPageHarness.WaitFor(cut, "Ask about 7 active documents.");

        await ChatPageHarness.TypeAsync(cut, "Keep me");
        _ = cut.InvokeAsync(() => h.EnterKey.OnEnter!(null));
        ChatPageHarness.WaitFor(cut, "Sign in required");

        await Assert.That(h.Drafts.Drafts[$"ragassistant.chat.draft.{id}"]).IsEqualTo("Keep me");

        var fresh = h.Render(id);
        cut.Dispose();
        fresh.WaitForState(() => fresh.FindComponent<QuestionInput>().Instance.Text == "Keep me", TimeSpan.FromSeconds(5));
    }

    // AC-13
    [Test]
    public async Task Send_conversationFull_offersANewConversationWithTheDraftCarriedOver()
    {
        await using var h = new ChatPageHarness();
        var id = Guid.NewGuid();
        h.Api.OnGet = i => ApiResult.Ok(ChatPageHarness.Detail(i));
        h.Answers.OnAsk = _ => FakeAnswerEvents.Failure(409, "chat.conversation_full", "This conversation is full. Start a new conversation.");
        var cut = h.Render(id);
        ChatPageHarness.WaitFor(cut, "Ask about 7 active documents.");

        await ChatPageHarness.TypeAsync(cut, "One more");
        _ = cut.InvokeAsync(() => h.EnterKey.OnEnter!(null));
        ChatPageHarness.WaitFor(cut, "This conversation is full.");
        cut.FindAll("fluent-button").First(b => b.TextContent.Trim() == "Start a new conversation").Click();

        await Assert.That(h.Ctx.Services.GetRequiredService<NavigationManager>().Uri).IsEqualTo("http://localhost/chat");
        await Assert.That(h.Drafts.Drafts["ragassistant.chat.draft.new"]).IsEqualTo("One more");
    }

    // AC-14
    [Test]
    public async Task PendingAnswer_showsTheNoticeAndDisablesTheInput()
    {
        await using var h = new ChatPageHarness();
        var question = ChatPageHarness.Question("Q");
        var pending = AnswerData.Assistant("pending", text: "") with { Sequence = 2, ReplyToMessageId = question.Id };
        h.Api.OnGet = i => ApiResult.Ok(ChatPageHarness.Detail(i, question, pending));

        var cut = h.Render(Guid.NewGuid());
        ChatPageHarness.WaitFor(cut, "An answer is being written in another tab or window.");

        await Assert.That(cut.FindComponent<FluentTextArea>().Instance.Disabled).IsTrue();
    }

    [Test]
    public async Task UnknownConversation_showsNotFound()
    {
        await using var h = new ChatPageHarness();
        h.Api.OnGet = _ => ApiResult.Fail<ConversationDetail>(new ApiError(404, "chat.conversation_not_found", "nope", new Dictionary<string, string[]>()));

        var cut = h.Render(Guid.NewGuid());
        ChatPageHarness.WaitFor(cut, "Conversation not found");

        await Assert.That(cut.Find("fluent-anchor-button, a[href='chat']").TextContent).Contains("Start a new conversation");
    }

    // AC-15
    [Test]
    public async Task List_showsTwentyThenLoadMoreAppendsTheRest()
    {
        await using var h = new ChatPageHarness();
        var all = Enumerable.Range(1, 25)
            .Select(i => new ConversationSummary(Guid.NewGuid(), i == 1 ? null : $"Conversation {i}", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(-i)))
            .ToArray();
        h.Api.OnList = page => ApiResult.Ok(new ConversationPage([.. all.Skip((page - 1) * 20).Take(20)], page, 20, 25));
        h.Api.OnGet = i => ApiResult.Ok(ChatPageHarness.Detail(i));

        var cut = h.Render(all[2].Id);
        cut.WaitForState(() => cut.FindAll(".conversation-link").Count == 20, TimeSpan.FromSeconds(5));
        await Assert.That(cut.FindAll(".conversation-link")[0].TextContent).Contains("New conversation");
        await Assert.That(cut.FindAll(".conversation-link[aria-current=page]").Count).IsEqualTo(1);

        cut.FindAll("fluent-button").First(b => b.TextContent.Trim() == "Load more").Click();
        cut.WaitForState(() => cut.FindAll(".conversation-link").Count == 25, TimeSpan.FromSeconds(5));

        await Assert.That(cut.FindAll("fluent-button").Any(b => b.TextContent.Trim() == "Load more")).IsFalse();
    }

    [Test]
    public async Task List_empty_saysNoConversationsYet()
    {
        await using var h = new ChatPageHarness();

        var cut = h.Render();
        ChatPageHarness.WaitFor(cut, "No conversations yet.");

        await Assert.That(cut.FindAll("fluent-button").Any(b => b.TextContent.Trim() == "New conversation")).IsTrue();
    }

    // AC-23 (the real layout is covered by the smoke test)
    [Test]
    public async Task NarrowScreen_listSitsBehindAButton()
    {
        await using var h = new ChatPageHarness();
        h.Viewport.Narrow = true;

        var cut = h.Render();
        ChatPageHarness.WaitFor(cut, "Ask about 7 active documents.");
        await Assert.That(cut.FindAll("nav.conversation-list").Count).IsEqualTo(0);

        cut.FindAll("fluent-button").First(b => b.TextContent.Trim() == "Conversations").Click();

        await Assert.That(cut.FindAll("nav.conversation-list").Count).IsEqualTo(1);
    }
}
