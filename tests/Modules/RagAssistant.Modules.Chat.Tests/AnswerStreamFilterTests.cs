using RagAssistant.Modules.Chat.Answering;

namespace RagAssistant.Modules.Chat.Tests;

internal sealed class AnswerStreamFilterTests
{
    private const string Sentinel = "INSUFFICIENT_EVIDENCE";

    private static (string Streamed, AnswerStreamFilter Filter) Run(int sources, params string[] fragments)
    {
        var filter = new AnswerStreamFilter(sources, Sentinel);
        var streamed = string.Concat(fragments.Select(filter.Push)) + filter.Flush();
        return (streamed, filter);
    }

    [Test]
    [Arguments("Rule [1] and [3].", "Rule [1] and [3].")]
    [Arguments("Rule [9] here.", "Rule  here.")]
    [Arguments("Rule [0].", "Rule .")]
    [Arguments("Empty [] stays.", "Empty [] stays.")]
    [Arguments("Words [abc] stay.", "Words [abc] stay.")]
    [Arguments("Spaces [ ] stay.", "Spaces [ ] stay.")]
    [Arguments("Trailing [1,] stays.", "Trailing [1,] stays.")]
    [Arguments("List [1, 3] here.", "List [1][3] here.")]
    [Arguments("List [2,7] here.", "List [2] here.")]
    [Arguments("All bad [8, 9] here.", "All bad  here.")]
    [Arguments("Nested [[2]] text.", "Nested [[2]] text.")]
    public async Task Push_markers_areValidatedNormalizedOrKeptAsText(string reply, string expected)
    {
        var (streamed, filter) = Run(3, reply);

        await Assert.That(streamed).IsEqualTo(expected);
        await Assert.That(filter.Text).IsEqualTo(expected);
    }

    [Test]
    public async Task Push_markerSplitAcrossFragments_isHeldUntilComplete()
    {
        var filter = new AnswerStreamFilter(3, Sentinel);

        var first = filter.Push("Rule [");
        var second = filter.Push("2");
        var third = filter.Push("] done.");

        await Assert.That(first).IsEqualTo("Rule ");
        await Assert.That(second).IsEqualTo(string.Empty);
        await Assert.That(third).IsEqualTo("[2] done.");
        await Assert.That(filter.CitedMarkers.ToArray().Same(2)).IsTrue();
    }

    [Test]
    public async Task Push_strippedNumbers_areCountedAndCitedMarkersAreDistinct()
    {
        var (_, filter) = Run(3, "A [1] b [1] c [5] d [2, 6].");

        await Assert.That(filter.StrippedMarkerCount).IsEqualTo(2);
        await Assert.That(filter.CitedMarkers.ToArray().Same(1, 2)).IsTrue();
    }

    [Test]
    public async Task Flush_unclosedBracket_isReleasedAsText()
    {
        var (streamed, filter) = Run(3, "See [1, 2");

        await Assert.That(streamed).IsEqualTo("See [1, 2");
        await Assert.That(filter.CitedMarkers.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Push_bracketLongerThanTheCap_isReleasedAsText()
    {
        var filter = new AnswerStreamFilter(3, Sentinel);
        var digits = "[" + string.Join(", ", Enumerable.Repeat("1", 12)); // 35 characters, never closed: released without a flush

        var released = filter.Push(digits);

        await Assert.That(released).IsEqualTo(digits);
    }

    [Test]
    [Arguments("INSUFFICIENT_EVIDENCE")]
    [Arguments("INSUFFICIENT_EVIDENCE.")]
    [Arguments("  insufficient_evidence\n")]
    public async Task Push_replyStartingWithTheSentinel_releasesNothing(string reply)
    {
        var (streamed, filter) = Run(3, reply);

        await Assert.That(streamed).IsEqualTo(string.Empty);
        await Assert.That(filter.SentinelHit).IsTrue();
    }

    [Test]
    public async Task Push_sentinelSplitAcrossFragments_holdsThenHits()
    {
        var filter = new AnswerStreamFilter(3, Sentinel);

        var first = filter.Push("INSUFF");
        var second = filter.Push("ICIENT_EVIDENCE. Sorry.");

        await Assert.That(first + second).IsEqualTo(string.Empty);
        await Assert.That(filter.SentinelHit).IsTrue();
        await Assert.That(filter.Push(" more text")).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Push_sentinelLaterInTheReply_isOrdinaryText()
    {
        var (streamed, filter) = Run(3, "The passages say INSUFFICIENT_EVIDENCE [1].");

        await Assert.That(streamed).IsEqualTo("The passages say INSUFFICIENT_EVIDENCE [1].");
        await Assert.That(filter.SentinelHit).IsFalse();
    }

    [Test]
    public async Task Push_wordThatOnlyStartsLikeTheSentinel_isReleasedOnceItDiverges()
    {
        var filter = new AnswerStreamFilter(3, Sentinel);

        var held = filter.Push("Insu");
        var released = filter.Push("rance covers it [1].");

        await Assert.That(held).IsEqualTo(string.Empty);
        await Assert.That(released).IsEqualTo("Insurance covers it [1].");
        await Assert.That(filter.SentinelHit).IsFalse();
    }

    [Test]
    public async Task Push_sentinelFollowedByALetter_isNotTheSentinel()
    {
        var (streamed, filter) = Run(3, "INSUFFICIENT_EVIDENCES are rare [1].");

        await Assert.That(filter.SentinelHit).IsFalse();
        await Assert.That(streamed).IsEqualTo("INSUFFICIENT_EVIDENCES are rare [1].");
    }

    [Test]
    public async Task Push_normalFirstToken_isReleasedAtOnce()
    {
        var filter = new AnswerStreamFilter(3, Sentinel);

        await Assert.That(filter.Push("Department")).IsEqualTo("Department");
    }
}
