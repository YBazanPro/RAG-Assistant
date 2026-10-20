using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using RagAssistant.Web.Client.Chat;
using RagAssistant.Web.Client.Documents;
using ChatPage = RagAssistant.Web.Client.Pages.Chat;

namespace RagAssistant.Web.Tests.Chat;

// Spec 020: asking from a link (AC-8), rename (AC-15), search (AC-17) and export (AC-18) on the chat page.
public sealed class ChatConveniencesUiTests
{
    private static ApiResult<ConversationDetail> Conversation(Guid id, string title) =>
        ApiResult.Ok(new ConversationDetail(id, title, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
        [
            new ChatMessage(Guid.NewGuid(), "user", 1, title, DateTimeOffset.UnixEpoch, null, null, null, null, null, null, null),
        ]));

    // AC-8
    [Test]
    public async Task AskLink_opensANewConversationAndAsksTheQuestionOnce()
    {
        await using var h = new ChatPageHarness();
        h.Answers.OnAsk = _ => FakeAnswerEvents.Failure(503);
        h.Ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/chat?ask=When%20is%20travel%20booked%3F");

        var cut = h.Ctx.Render<ChatPage>();
        cut.WaitForState(() => h.Answers.AskCalls.Count == 1, TimeSpan.FromSeconds(5));

        await Assert.That(h.Api.CreateCalls).IsEqualTo(1);
        await Assert.That(h.Answers.AskCalls[0].Question).IsEqualTo("When is travel booked?");
        await Assert.That(h.Ctx.Services.GetRequiredService<NavigationManager>().Uri).DoesNotContain("ask=");
    }

    [Test]
    public async Task HomeAskLink_escapesTheQuestion() =>
        await Assert.That(Client.Pages.Home.AskLink("Congés payés ?")).IsEqualTo("chat?ask=Cong%C3%A9s%20pay%C3%A9s%20%3F");

    // AC-15
    [Test]
    public async Task Rename_savesTheTitle_andShowsItInTheHeader()
    {
        await using var h = new ChatPageHarness();
        var id = Guid.NewGuid();
        h.Api.OnGet = _ => Conversation(id, "First question");

        var cut = h.Render(id);
        ChatPageHarness.WaitFor(cut, "Rename");
        cut.FindAll("fluent-button").Single(b => b.TextContent.Trim() == "Rename").Click();
        var input = cut.FindComponents<FluentTextInput>().Single(c => c.Instance.Label == "Conversation title");
        await cut.InvokeAsync(() => input.Instance.ValueChanged.InvokeAsync("Budget notes"));
        cut.FindAll("fluent-button").Single(b => b.TextContent.Trim() == "Save").Click();
        ChatPageHarness.WaitFor(cut, "Budget notes");

        await Assert.That(h.Api.RenameCalls.Single()).IsEqualTo((id, "Budget notes"));
        await Assert.That(cut.Find(".conversation-heading").TextContent).IsEqualTo("Budget notes");
    }

    [Test]
    public async Task RenameRefused_showsTheFieldError()
    {
        await using var h = new ChatPageHarness();
        var id = Guid.NewGuid();
        h.Api.OnGet = _ => Conversation(id, "First question");
        h.Api.OnRename = (_, _) => ApiResult.Fail<ConversationSummary>(
            new ApiError(400, "validation.failed", "One or more validation errors occurred.", new Dictionary<string, string[]> { ["title"] = ["The title is at most 200 characters."] }));

        var cut = h.Render(id);
        ChatPageHarness.WaitFor(cut, "Rename");
        cut.FindAll("fluent-button").Single(b => b.TextContent.Trim() == "Rename").Click();
        cut.FindAll("fluent-button").Single(b => b.TextContent.Trim() == "Save").Click();
        ChatPageHarness.WaitFor(cut, "The title is at most 200 characters.");

        await Assert.That(cut.FindComponents<FluentTextInput>().Any(c => c.Instance.Label == "Conversation title")).IsTrue();
    }

    // AC-18
    [Test]
    public async Task Export_isAPlainDownloadLinkThroughTheBff()
    {
        await using var h = new ChatPageHarness();
        var id = Guid.NewGuid();
        h.Api.OnGet = _ => Conversation(id, "First question");

        var cut = h.Render(id);
        ChatPageHarness.WaitFor(cut, "Export as Markdown");

        var link = cut.Find("a.export-conversation");
        await Assert.That(link.GetAttribute("href")).IsEqualTo($"api/chat/conversations/{id}/export");
        // download keeps Blazor's router from treating the click as navigation.
        await Assert.That(link.HasAttribute("download")).IsTrue();
    }

    // AC-17
    [Test]
    public async Task Search_reloadsTheListWithTheQuery_andSaysWhenNothingMatches()
    {
        await using var h = new ChatPageHarness();
        h.Api.OnList = page => ApiResult.Ok(new ConversationPage([new ConversationSummary(Guid.NewGuid(), "Travel", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)], page, 20, 1));

        var cut = h.Render();
        ChatPageHarness.WaitFor(cut, "Travel");
        h.Api.OnList = page => ApiResult.Ok(new ConversationPage([], page, 20, 0));
        var search = cut.FindComponents<FluentTextInput>().Single(c => c.Instance.Label == "Search conversations");
        await cut.InvokeAsync(() => search.Instance.ValueChanged.InvokeAsync("conges"));
        ChatPageHarness.WaitFor(cut, "No conversations match.");

        await Assert.That(h.Api.SearchCalls[^1]).IsEqualTo("conges");
    }
}
