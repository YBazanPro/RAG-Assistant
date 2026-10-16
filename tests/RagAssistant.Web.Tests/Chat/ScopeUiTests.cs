using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using RagAssistant.Web.Client.Chat;
using RagAssistant.Web.Client.Documents;
using RagAssistant.Web.Tests.Documents;
using ChatPage = RagAssistant.Web.Client.Pages.Chat;

namespace RagAssistant.Web.Tests.Chat;

// Spec 015, AC-17 and the scope bar: choosing the documents and tags a conversation searches.
public class ScopeUiTests
{
    private static readonly Guid Budget = Guid.Parse("00000000-0000-0000-0000-00000000b001");
    private static readonly Guid Travel = Guid.Parse("00000000-0000-0000-0000-00000000b002");

    private static void SeedDocuments(FakeDocumentsApi documents) =>
        documents.OnList = _ => ApiResult.Ok(Samples.Page(Samples.Summary("Budget Policy", id: Budget), Samples.Summary("Travel Policy", id: Travel)));

    private static AngleSharp.Dom.IElement DialogButton(IRenderedComponent<ChatPage> cut, string text) =>
        cut.FindAll("fluent-dialog fluent-button").First(b => b.TextContent.Trim() == text);

    private static async Task CheckAsync(IRenderedComponent<ChatPage> cut, string label) =>
        await cut.InvokeAsync(() => cut.FindComponents<FluentCheckbox>().First(c => c.Instance.Label == label).Instance.ValueChanged.InvokeAsync(true));

    private static ConversationScope ScopeOf(params ScopeDocument[] documents) =>
        new([.. documents.Select(d => d.Id)], [], [.. documents.Where(d => !d.Available).Select(d => d.Id)], documents);

    // AC-17: the picker lists documents, toggles them and hands back the choice
    [Test]
    public async Task Picker_listsActiveDocuments_andApplyReturnsTheChosenOnes()
    {
        var ctx = ChatTestData.NewContext(out _, out _);
        await using var disposeContext = ctx;
        var documents = new FakeDocumentsApi();
        SeedDocuments(documents);
        ctx.Services.AddSingleton<IDocumentsApi>(documents);
        ScopeChoice? applied = null;
        var cut = ctx.Render<ScopePicker>(p => p
            .Add(c => c.Open, true)
            .Add(c => c.OnApply, EventCallback.Factory.Create<ScopeChoice>(ctx, c => applied = c)));
        cut.WaitForState(() => cut.FindComponents<FluentCheckbox>().Count == 2, TimeSpan.FromSeconds(5));

        await cut.InvokeAsync(() => cut.FindComponents<FluentCheckbox>().First(c => c.Instance.Label == "Travel Policy").Instance.ValueChanged.InvokeAsync(true));
        cut.FindAll("fluent-dialog fluent-button").First(b => b.TextContent.Trim() == "Apply").Click();

        await Assert.That(documents.ListCalls[0].Status).IsEqualTo("active");
        await Assert.That(applied).IsNotNull();
        await Assert.That(applied!.Documents.Select(d => d.Id).SequenceEqual([Travel])).IsTrue();
        await Assert.That(applied.Documents[0].Title).IsEqualTo("Travel Policy");
    }

    [Test]
    public async Task Picker_startsFromTheCurrentScope_andShowsChosenDocumentsSeparately()
    {
        var ctx = ChatTestData.NewContext(out _, out _);
        await using var disposeContext = ctx;
        var documents = new FakeDocumentsApi();
        SeedDocuments(documents);
        ctx.Services.AddSingleton<IDocumentsApi>(documents);

        var cut = ctx.Render<ScopePicker>(p => p.Add(c => c.Open, true).Add(c => c.Scope, ScopeOf(new ScopeDocument(Budget, "Budget Policy", true))));
        cut.WaitForState(() => cut.FindAll("[aria-label='Chosen documents']").Count == 1, TimeSpan.FromSeconds(5));

        var chosen = cut.Find("[aria-label='Chosen documents']").TextContent;
        var others = cut.Find("[aria-label='Documents']").TextContent;
        await Assert.That(chosen).Contains("Budget Policy");
        await Assert.That(others).DoesNotContain("Budget Policy");
        await Assert.That(others).Contains("Travel Policy");
        await Assert.That(cut.Markup).Contains("1 of 25 documents, 0 of 20 tags");
    }

    [Test]
    public async Task Bar_withoutScope_saysItSearchesEverything()
    {
        var ctx = ChatTestData.NewContext(out _, out _);
        await using var disposeContext = ctx;

        var cut = ctx.Render<ScopeBar>(p => p.Add(c => c.Scope, ConversationScope.Empty));

        await Assert.That(cut.Find(".scope-summary").TextContent).IsEqualTo("Searching all documents.");
        await Assert.That(cut.FindAll(".scope-chip").Count).IsEqualTo(0);
        await Assert.That(cut.FindAll(".scope-clear").Count).IsEqualTo(0);
    }

    // AC-6: an unavailable document is flagged on its chip
    [Test]
    public async Task Page_showsChipsForTheStoredScope_andFlagsUnavailableDocuments()
    {
        await using var h = new ChatPageHarness();
        var id = Guid.NewGuid();
        var scope = new ConversationScope([Budget, Travel], ["finance"], [Travel], [new(Budget, "Budget Policy", true), new(Travel, "Old Travel", false)]);
        h.Api.OnGet = i => ApiResult.Ok(ChatPageHarness.Detail(i) with { Scope = scope });

        var cut = h.Render(id);
        cut.WaitForState(() => cut.FindAll(".scope-chip").Count == 3, TimeSpan.FromSeconds(5));

        var chips = cut.FindAll(".scope-chip").Select(c => c.TextContent.Trim()).ToArray();
        await Assert.That(chips.Any(c => c == "Budget Policy")).IsTrue();
        await Assert.That(chips.Any(c => c == "Old Travel (unavailable)")).IsTrue();
        await Assert.That(chips.Any(c => c == "tag: finance")).IsTrue();
        await Assert.That(cut.Find(".scope-summary").TextContent).Contains("Searching 2 documents");
    }

    [Test]
    public async Task Page_applyingAScope_savesItOnTheConversation_andClearingRemovesIt()
    {
        await using var h = new ChatPageHarness();
        SeedDocuments(h.Documents);
        var id = Guid.NewGuid();
        h.Api.OnGet = i => ApiResult.Ok(ChatPageHarness.Detail(i));
        var cut = h.Render(id);
        ChatPageHarness.WaitFor(cut, "Searching all documents.");

        cut.Find(".scope-choose").Click();
        cut.WaitForState(() => cut.FindComponents<FluentCheckbox>().Count == 2, TimeSpan.FromSeconds(5));
        await CheckAsync(cut, "Budget Policy");
        DialogButton(cut, "Apply").Click();
        cut.WaitForState(() => cut.FindAll(".scope-chip").Count == 1, TimeSpan.FromSeconds(5));

        await Assert.That(h.Api.ScopeCalls.Count).IsEqualTo(1);
        await Assert.That(h.Api.ScopeCalls[0].Conversation).IsEqualTo(id);
        await Assert.That(h.Api.ScopeCalls[0].Documents.SequenceEqual([Budget])).IsTrue();
        await Assert.That(cut.Find(".scope-chip").TextContent.Trim()).IsEqualTo("Document " + Budget.ToString()[..4]);

        cut.Find(".scope-clear").Click();
        cut.WaitForState(() => cut.FindAll(".scope-chip").Count == 0, TimeSpan.FromSeconds(5));

        await Assert.That(h.Api.ScopeCalls.Count).IsEqualTo(2);
        await Assert.That(h.Api.ScopeCalls[1].Documents.Length).IsEqualTo(0);
        await Assert.That(cut.Find(".scope-summary").TextContent).IsEqualTo("Searching all documents.");
    }

    [Test]
    public async Task Page_whenSavingTheScopeFails_showsAMessageAndKeepsTheOldScope()
    {
        await using var h = new ChatPageHarness();
        SeedDocuments(h.Documents);
        var id = Guid.NewGuid();
        h.Api.OnGet = i => ApiResult.Ok(ChatPageHarness.Detail(i));
        h.Api.OnScope = (_, _) => ApiResult.Fail<ConversationScope>(new ApiError(500, "x", "boom", new Dictionary<string, string[]>()));
        var cut = h.Render(id);
        ChatPageHarness.WaitFor(cut, "Searching all documents.");

        cut.Find(".scope-choose").Click();
        cut.WaitForState(() => cut.FindComponents<FluentCheckbox>().Count == 2, TimeSpan.FromSeconds(5));
        await CheckAsync(cut, "Budget Policy");
        DialogButton(cut, "Apply").Click();
        cut.WaitForState(() => cut.FindAll(".scope-error").Count == 1, TimeSpan.FromSeconds(5));

        await Assert.That(cut.Find(".scope-error").TextContent).Contains("could not be saved");
        await Assert.That(cut.FindAll(".scope-chip").Count).IsEqualTo(0);
    }

    // The conversation does not exist yet: the scope waits and is saved before the first question is asked
    [Test]
    public async Task Page_newConversation_savesTheChosenScopeBeforeAskingTheFirstQuestion()
    {
        await using var h = new ChatPageHarness();
        SeedDocuments(h.Documents);
        var id = Guid.NewGuid();
        var script = new ScriptedEvents();
        h.Api.OnCreate = () => ApiResult.Ok(new ConversationSummary(id, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));
        h.Api.OnGet = i => ApiResult.Ok(ChatPageHarness.Detail(i));
        h.Answers.OnAsk = _ => FakeAnswerEvents.Stream(script);
        var cut = h.Render();
        ChatPageHarness.WaitFor(cut, "Searching all documents.");

        cut.Find(".scope-choose").Click();
        cut.WaitForState(() => cut.FindComponents<FluentCheckbox>().Count == 2, TimeSpan.FromSeconds(5));
        await CheckAsync(cut, "Travel Policy");
        DialogButton(cut, "Apply").Click();
        cut.WaitForState(() => cut.FindAll(".scope-chip").Count == 1, TimeSpan.FromSeconds(5));
        await Assert.That(h.Api.CreateCalls).IsEqualTo(0);
        await Assert.That(h.Api.ScopeCalls.Count).IsEqualTo(0);

        await ChatPageHarness.TypeAsync(cut, "Who approves a budget exception?");
        _ = cut.InvokeAsync(() => h.EnterKey.OnEnter!(null));
        await ScriptedEvents.WaitUntilAsync(() => h.Answers.AskCalls.Count == 1);

        await Assert.That(h.Api.CreateCalls).IsEqualTo(1);
        await Assert.That(h.Api.ScopeCalls.Count).IsEqualTo(1);
        await Assert.That(h.Api.ScopeCalls[0].Conversation).IsEqualTo(id);
        await Assert.That(h.Api.ScopeCalls[0].Documents.SequenceEqual([Travel])).IsTrue();
        script.End();
    }

    // The scope is part of the question: it cannot be changed while an answer is being written
    [Test]
    public async Task Bar_isDisabledWhileAnAnswerIsStreaming()
    {
        await using var h = new ChatPageHarness();
        var id = Guid.NewGuid();
        var script = new ScriptedEvents();
        h.Api.OnGet = i => ApiResult.Ok(ChatPageHarness.Detail(i));
        h.Answers.OnAsk = _ => FakeAnswerEvents.Stream(script);
        var cut = h.Render(id);
        ChatPageHarness.WaitFor(cut, "Searching all documents.");

        await ChatPageHarness.TypeAsync(cut, "Who approves a budget exception?");
        _ = cut.InvokeAsync(() => h.EnterKey.OnEnter!(null));
        script.Send(new QuestionAccepted(id, Guid.NewGuid(), Guid.NewGuid()));
        cut.WaitForState(() => cut.Find(".scope-choose").HasAttribute("disabled"), TimeSpan.FromSeconds(5));

        await Assert.That(cut.Find(".scope-choose").HasAttribute("disabled")).IsTrue();
        script.End();
    }
}
