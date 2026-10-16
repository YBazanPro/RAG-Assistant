using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using RagAssistant.Web.Client.Chat;

namespace RagAssistant.Web.Tests.Chat;

// Spec 013, AC-9 to AC-11: the safe Markdown subset, the citation list and the source preview.
public class CitationComponentTests
{
    // AC-9
    [Test]
    public async Task AnswerText_htmlHeadingAndLink_renderAsLiteralText()
    {
        await using var ctx = ChatTestData.NewContext(out _, out _);

        var cut = ctx.Render<AnswerText>(p => p.Add(c => c.Text, "<script>alert(1)</script>\n# Heading\n[link](http://x)"));

        await Assert.That(cut.FindAll("script").Count).IsEqualTo(0);
        await Assert.That(cut.FindAll("a").Count).IsEqualTo(0);
        await Assert.That(cut.FindAll("h1").Count).IsEqualTo(0);
        await Assert.That(cut.Markup).Contains("&lt;script&gt;alert(1)&lt;/script&gt;");
        await Assert.That(cut.Markup).Contains("# Heading");
        await Assert.That(cut.Markup).Contains("[link](http://x)");
    }

    // AC-9
    [Test]
    public async Task AnswerText_boldAndLists_renderAsElements()
    {
        await using var ctx = ChatTestData.NewContext(out _, out _);

        var cut = ctx.Render<AnswerText>(p => p.Add(c => c.Text, "This is **bold** and *slanted*.\n\n- one\n- two\n\n1. first\n2. second"));

        await Assert.That(cut.Find("strong").TextContent).IsEqualTo("bold");
        await Assert.That(cut.Find("em").TextContent).IsEqualTo("slanted");
        await Assert.That(cut.FindAll("ul > li").Count).IsEqualTo(2);
        await Assert.That(cut.FindAll("ol > li").Count).IsEqualTo(2);
    }

    // AC-4: a marker is a button only once it is complete and has a source
    [Test]
    public async Task AnswerText_markers_becomeButtonsOnlyWhenCompleteAndResolvable()
    {
        await using var ctx = ChatTestData.NewContext(out _, out _);
        var sources = new[] { ChatTestData.Citation(1) };

        var partial = ctx.Render<AnswerText>(p => p.Add(c => c.Text, "Head [1").Add(c => c.Sources, sources));
        await Assert.That(partial.FindAll("button").Count).IsEqualTo(0);

        var complete = ctx.Render<AnswerText>(p => p.Add(c => c.Text, "Head [1] and [2].").Add(c => c.Sources, sources));
        await Assert.That(complete.FindAll("button").Count).IsEqualTo(1);
        await Assert.That(complete.Markup).Contains("[2].");
    }

    [Test]
    public async Task AnswerText_markerClick_selectsThatMarker()
    {
        await using var ctx = ChatTestData.NewContext(out _, out _);
        int? selected = null;

        var cut = ctx.Render<AnswerText>(p => p
            .Add(c => c.Text, "See [1].")
            .Add(c => c.Sources, [ChatTestData.Citation(1)])
            .Add(c => c.OnCitationSelected, EventCallback.Factory.Create<int>(this, m => selected = m)));
        cut.Find("button").Click();

        await Assert.That(selected).IsEqualTo(1);
    }

    [Test]
    public async Task CitationItem_showsLocationAndHasTheAccessibleName()
    {
        await using var ctx = ChatTestData.NewContext(out _, out _);

        var cut = ctx.Render<CitationItem>(p => p.Add(c => c.Citation, ChatTestData.Citation()));
        var main = cut.Find("button.citation-item-main");

        await Assert.That(main.GetAttribute("aria-label")).IsEqualTo("Source 1: Budget Exception Policy, Approval › Thresholds, page 4");
        await Assert.That(main.TextContent).Contains("Budget Exception Policy");
        await Assert.That(main.TextContent).Contains("Approval › Thresholds");
        await Assert.That(main.TextContent).Contains("version 3 · p. 4");
    }

    // AC-4
    [Test]
    public async Task CitationList_listsGivenSourcesInMarkerOrder()
    {
        await using var ctx = ChatTestData.NewContext(out _, out _);

        var cut = ctx.Render<CitationList>(p => p.Add(c => c.Citations, [ChatTestData.Citation(3), ChatTestData.Citation(1)]));

        var labels = cut.FindAll("button.citation-item-main").Select(b => b.GetAttribute("aria-label")!.Split(':')[0]).ToArray();
        await Assert.That(labels).IsEquivalentTo(["Source 1", "Source 3"]);
    }

    // AC-10
    [Test]
    public async Task SourcePreview_showsTheSnapshotAndTheTwoLinks()
    {
        await using var ctx = ChatTestData.NewContext(out _, out _);
        var citation = ChatTestData.Citation();

        var cut = ctx.Render<SourcePreview>(p => p
            .Add(c => c.Citation, citation)
            .Add(c => c.CitedAt, new DateTimeOffset(2026, 10, 15, 9, 30, 0, TimeSpan.Zero)));

        await Assert.That(cut.Markup).Contains("Budget Exception Policy");
        await Assert.That(cut.Markup).Contains("version 3");
        await Assert.That(cut.Markup).Contains("p. 4");
        await Assert.That(cut.Markup).Contains("As cited on");
        await Assert.That(cut.Find("pre").TextContent).IsEqualTo("Exceptions above 5000 need approval.");
        await Assert.That(cut.Find("a.open-document-link").GetAttribute("href")).IsEqualTo($"documents/{citation.DocumentId}?version=3&passage={citation.PassageId}");
        await Assert.That(cut.Find("a.open-page-link").GetAttribute("href")).IsEqualTo($"api/documents/{citation.DocumentId}/versions/3/file#page=4");
    }

    [Test]
    public async Task SourcePreview_whileStreaming_saysThePassageComesLater()
    {
        await using var ctx = ChatTestData.NewContext(out _, out _);

        var cut = ctx.Render<SourcePreview>(p => p.Add(c => c.Citation, ChatTestData.Citation(text: null)));

        await Assert.That(cut.FindAll("pre").Count).IsEqualTo(0);
        await Assert.That(cut.Markup).Contains("The passage is shown when the answer completes.");
    }

    // AC-10
    [Test]
    public async Task SourcePreview_closeAndEscape_returnFocusToTheOpener()
    {
        await using var ctx = ChatTestData.NewContext(out _, out var scroller);
        var closed = 0;

        var cut = ctx.Render<SourcePreview>(p => p
            .Add(c => c.Citation, ChatTestData.Citation())
            .Add(c => c.ReturnFocusId, "citation-1")
            .Add(c => c.OnClose, EventCallback.Factory.Create(this, () => closed++)));
        cut.Find("aside").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });
        cut.FindComponent<FluentButton>().Find("fluent-button").Click();

        await Assert.That(closed).IsEqualTo(2);
        await Assert.That(scroller.Focused).IsEquivalentTo(["citation-1", "citation-1"]);
    }

    // AC-11
    [Test]
    public async Task CopyReference_copiesTheReferenceAndTheAbsoluteLink()
    {
        await using var ctx = ChatTestData.NewContext(out var clipboard, out _);
        var citation = ChatTestData.Citation();

        var cut = ctx.Render<CopyReference>(p => p.Add(c => c.Citation, citation));
        cut.Find("fluent-button").Click();

        var expected = $"Budget Exception Policy — Approval › Thresholds (version 3, p. 4)\nhttp://localhost/documents/{citation.DocumentId}?version=3&passage={citation.PassageId}";
        await Assert.That(clipboard.Copied).IsEquivalentTo([expected]);
        await Assert.That(cut.Find("[role=status]").TextContent).IsEqualTo("Copied");
    }

    // AC-11
    [Test]
    public async Task CopyReference_blockedClipboard_showsTheSameTextInAField()
    {
        await using var ctx = ChatTestData.NewContext(out var clipboard, out _);
        clipboard.Blocked = true;

        var cut = ctx.Render<CopyReference>(p => p.Add(c => c.Citation, ChatTestData.Citation()));
        cut.Find("fluent-button").Click();

        var field = cut.FindComponent<FluentTextInput>().Instance;
        await Assert.That(field.Value).StartsWith("Budget Exception Policy — Approval › Thresholds (version 3, p. 4)\nhttp://localhost/documents/");
        await Assert.That(cut.FindAll("[role=status]").Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("no_evidence", "The documents don't contain enough information to answer this.")]
    [Arguments("model_declined", "The passages found don't answer this question.")]
    public async Task RefusalNotice_showsTheMessageForTheReason(string reason, string message)
    {
        await using var ctx = ChatTestData.NewContext(out _, out _);

        var cut = ctx.Render<RefusalNotice>(p => p.Add(c => c.Reason, reason));

        await Assert.That(cut.Markup).Contains(message);
        await Assert.That(cut.Find("a").GetAttribute("href")).IsEqualTo("documents");
        await Assert.That(cut.FindAll("fluent-message-bar").Count).IsEqualTo(0);
    }
}
