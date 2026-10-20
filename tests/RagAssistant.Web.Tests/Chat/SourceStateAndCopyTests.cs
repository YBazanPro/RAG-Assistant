using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using RagAssistant.Web.Client.Chat;

namespace RagAssistant.Web.Tests.Chat;

// Spec 020, AC-10 to AC-14 (badges and preview) and AC-19 (copy answer with sources).
public sealed class SourceStateAndCopyTests
{
    [Test]
    [Arguments("newer-unchanged", "Newer version")]
    [Arguments("newer-changed", "Changed in newer version")]
    [Arguments("archived", "Archived")]
    public async Task CitationItem_showsTheStateBadge_andSaysItInTheAccessibleName(string state, string badge)
    {
        await using var ctx = ChatTestData.NewContext(out _, out _);

        var cut = ctx.Render<CitationItem>(p => p.Add(c => c.Citation, ChatTestData.Citation() with { SourceState = state }));

        await Assert.That(cut.Find(".citation-state").TextContent).Contains(badge);
        await Assert.That(cut.Find(".citation-item-main").GetAttribute("aria-label")!).EndsWith($", {badge.ToLowerInvariant()}");
    }

    // AC-14
    [Test]
    public async Task CitationItem_ofACurrentSource_hasNoBadge()
    {
        await using var ctx = ChatTestData.NewContext(out _, out _);

        var cut = ctx.Render<CitationItem>(p => p.Add(c => c.Citation, ChatTestData.Citation()));

        await Assert.That(cut.FindAll(".citation-state").Count).IsEqualTo(0);
    }

    // AC-11
    [Test]
    public async Task SourcePreview_ofAChangedSource_saysSo_andOffersTheCurrentVersion_keepingTheSnapshot()
    {
        await using var ctx = ChatTestData.NewContext(out _, out _);
        var citation = ChatTestData.Citation() with { SourceState = SourceStates.NewerChanged };

        var cut = ctx.Render<SourcePreview>(p => p.Add(c => c.Citation, citation));

        await Assert.That(cut.Markup).Contains("This source has changed since the answer.");
        await Assert.That(cut.Find(".open-current-version").GetAttribute("href")).IsEqualTo($"documents/{citation.DocumentId}");
        await Assert.That(cut.Find(".passage-text").TextContent).IsEqualTo("Exceptions above 5000 need approval.");
    }

    [Test]
    public async Task SourcePreview_ofAnUnchangedPassage_saysSo()
    {
        await using var ctx = ChatTestData.NewContext(out _, out _);

        var cut = ctx.Render<SourcePreview>(p => p.Add(c => c.Citation, ChatTestData.Citation() with { SourceState = SourceStates.NewerUnchanged }));

        await Assert.That(cut.Markup).Contains("A newer version contains this passage unchanged.");
        await Assert.That(cut.FindAll(".open-current-version").Count).IsEqualTo(1);
    }

    // AC-19
    [Test]
    public async Task CopyAnswer_putsTheTextAndANumberedSourceListOnTheClipboard()
    {
        await using var ctx = ChatTestData.NewContext(out var clipboard, out _);
        Citation[] cited =
        [
            ChatTestData.Citation(1),
            ChatTestData.Citation(2, title: "Travel Policy") with { SectionPath = [], PageStart = null, SourceState = SourceStates.NewerChanged },
        ];

        var cut = ctx.Render<CopyAnswer>(p => p.Add(c => c.Text, "Finance approves [1][2].").Add(c => c.Cited, cited));
        cut.Find("fluent-button").Click();

        await Assert.That(clipboard.Copied.Single()).IsEqualTo(
            "Finance approves [1][2].\n\nSources:\n\n"
            + "- [1] Budget Exception Policy — Approval › Thresholds (version 3, page 4)\n"
            + "- [2] Travel Policy (version 3) — changed in a newer version");
        await Assert.That(cut.Markup).Contains("Copied");
    }

    [Test]
    public async Task CopyAnswer_withABlockedClipboard_showsTheTextToSelect()
    {
        await using var ctx = ChatTestData.NewContext(out var clipboard, out _);
        clipboard.Blocked = true;

        var cut = ctx.Render<CopyAnswer>(p => p.Add(c => c.Text, "Answer [1].").Add(c => c.Cited, [ChatTestData.Citation(1)]));
        cut.Find("fluent-button").Click();

        var fallback = cut.FindComponent<FluentTextArea>().Instance;
        await Assert.That(fallback.Value!).StartsWith("Answer [1].\n\nSources:");
        await Assert.That(fallback.ReadOnly).IsTrue();
    }

    [Test]
    public async Task SourceList_ofAnUnavailableSource_hidesItsTitle() =>
        await Assert.That(SourceListText.Line(ChatTestData.Citation(4) with { SourceState = SourceStates.Unavailable }))
            .IsEqualTo("- [4] Source no longer available");
}
