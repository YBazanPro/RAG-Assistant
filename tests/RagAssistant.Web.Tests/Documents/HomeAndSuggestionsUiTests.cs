using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using RagAssistant.Security;
using RagAssistant.Web.Client.Documents;
using RagAssistant.Web.Tests.Chat;
using HomePage = RagAssistant.Web.Client.Pages.Home;

namespace RagAssistant.Web.Tests.Documents;

// Spec 020: the home page (value, corpus summary, suggestions, recent documents) and the owner's suggested questions.
public sealed class HomeAndSuggestionsUiTests
{
    private static (BunitContext Ctx, FakeDocumentsApi Api) NewContext(params string[] policies)
    {
        var ctx = ChatTestData.NewContext(out _, out _);
        var api = new FakeDocumentsApi();
        ctx.Services.AddSingleton<IDocumentsApi>(api);
        ctx.AddAuthorization().SetAuthorized("someone").SetPolicies(policies.Length == 0 ? [PolicyNames.KnowledgeSeeker] : policies);
        return (ctx, api);
    }

    // AC-7 and AC-8 (the link half; the chat page side is ChatConveniencesUiTests)
    [Test]
    public async Task Home_showsTheCorpusSummarySuggestionsAndRecentDocuments()
    {
        var (ctx, api) = NewContext();
        await using var _ = ctx;
        var recent = Guid.NewGuid();
        api.OnSummary = () => ApiResult.Ok(new CorpusSummary(
            3, [new LanguageCount("en", 2), new LanguageCount("fr", 1)], DateTimeOffset.UnixEpoch, [new RecentDocument(recent, "Guide des congés", DateTimeOffset.UnixEpoch, "fr")]));
        api.OnSuggestions = _ => ApiResult.Ok<IReadOnlyList<SuggestedQuestion>>([new SuggestedQuestion("Combien de jours de congés ?", recent, "Guide des congés", "fr")]);

        var cut = ctx.Render<HomePage>();
        cut.WaitForState(() => cut.Markup.Contains("Try asking"), TimeSpan.FromSeconds(5));

        await Assert.That(cut.Markup).Contains("3 active documents: 2 in English, 1 in French.");
        var ask = cut.FindComponents<FluentAnchorButton>().Single(b => b.Instance.Href!.StartsWith("chat?ask=", StringComparison.Ordinal));
        await Assert.That(ask.Instance.Href).IsEqualTo(HomePage.AskLink("Combien de jours de congés ?"));
        await Assert.That(cut.FindComponents<FluentLink>().Any(l => l.Instance.Href == $"documents/{recent}")).IsTrue();
        await Assert.That(cut.Find(".readme-link").GetAttribute("href")).IsEqualTo(HomePage.ReadmeUrl);
    }

    [Test]
    public async Task Home_withNoReadableDocument_showsTheEmptyState_withImportForContentOwners()
    {
        var (ctx, _) = NewContext(PolicyNames.KnowledgeSeeker, PolicyNames.ContentOwner);
        await using var _c = ctx;

        var cut = ctx.Render<HomePage>();
        cut.WaitForState(() => cut.Markup.Contains("No documents yet"), TimeSpan.FromSeconds(5));

        await Assert.That(cut.FindComponents<FluentAnchorButton>().Any(b => b.Instance.Href == "documents")).IsTrue();
    }

    [Test]
    public async Task Home_withNoReadableDocument_hidesImportFromKnowledgeSeekers()
    {
        var (ctx, _) = NewContext();
        await using var _c = ctx;

        var cut = ctx.Render<HomePage>();
        cut.WaitForState(() => cut.Markup.Contains("No documents yet"), TimeSpan.FromSeconds(5));

        await Assert.That(cut.FindComponents<FluentAnchorButton>().Any(b => b.Instance.Href == "documents")).IsFalse();
    }

    // AC-5 (the page half)
    [Test]
    public async Task SuggestionsPanel_letsTheOwnerEditTheQuestions_withTheDocumentRevision()
    {
        var (ctx, api) = NewContext(PolicyNames.KnowledgeSeeker, PolicyNames.ContentOwner);
        await using var _ = ctx;
        var document = Samples.Detail(Guid.NewGuid(), revision: 4) with { SuggestedQuestions = ["Old question?"], SuggestionsStatus = "ready" };
        DocumentInfo? saved = null;

        var cut = ctx.Render<SuggestedQuestionsPanel>(p => p
            .Add(c => c.Document, document)
            .Add(c => c.CanEdit, true)
            .Add(c => c.OnSaved, EventCallback.Factory.Create<DocumentInfo>(this, d => saved = d)));
        cut.FindAll("fluent-button").Single(b => b.TextContent.Trim() == "Edit questions").Click();
        var inputs = cut.FindComponents<FluentTextInput>();
        await cut.InvokeAsync(() => inputs[1].Instance.ValueChanged.InvokeAsync("New question?"));
        cut.FindAll("fluent-button").Single(b => b.TextContent.Trim() == "Save questions").Click();

        var call = api.SuggestionUpdateCalls.Single();
        await Assert.That(call.Revision).IsEqualTo(4);
        await Assert.That(call.Questions.SequenceEqual(["Old question?", "New question?"])).IsTrue();
        await Assert.That(saved!.SuggestionsStatus).IsEqualTo("edited");
    }

    [Test]
    public async Task SuggestionsPanel_forAReader_hasNoEditButton_andSaysWhyThereAreNone()
    {
        var (ctx, _) = NewContext();
        await using var _c = ctx;
        var document = Samples.Detail(Guid.NewGuid()) with { SuggestionsStatus = "failed", SuggestionsError = "model_unavailable" };

        var cut = ctx.Render<SuggestedQuestionsPanel>(p => p.Add(c => c.Document, document));

        await Assert.That(cut.Markup).Contains("Questions could not be suggested (model_unavailable).");
        await Assert.That(cut.FindAll("fluent-button").Any(b => b.TextContent.Trim() == "Edit questions")).IsFalse();
    }
}
