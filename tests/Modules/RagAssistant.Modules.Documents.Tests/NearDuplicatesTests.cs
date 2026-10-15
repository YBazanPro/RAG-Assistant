using RagAssistant.Modules.Documents.Retrieval;

namespace RagAssistant.Modules.Documents.Tests;

internal sealed class NearDuplicatesTests
{
    [Test]
    public async Task Shingles_lowercasesAndSplitsOnPunctuation()
    {
        var shingles = NearDuplicates.Shingles("Budget, EXCEPTIONS: need-approval");

        await Assert.That(shingles.Order(StringComparer.Ordinal).ToArray())
            .IsEquivalentTo(["budget exceptions need", "exceptions need approval"]);
    }

    [Test]
    [Arguments("", 0)]
    [Arguments("  ...  ", 0)]
    [Arguments("one", 1)]
    [Arguments("one two", 1)]
    [Arguments("one two three", 1)]
    [Arguments("one two three four", 2)]
    public async Task Shingles_shortTexts_formOneShingle(string text, int expected)
    {
        await Assert.That(NearDuplicates.Shingles(text).Count).IsEqualTo(expected);
    }

    [Test]
    public async Task Jaccard_identicalText_isOne()
    {
        var a = NearDuplicates.Shingles("the approval threshold is ten thousand dollars");
        var b = NearDuplicates.Shingles("The approval threshold is ten thousand dollars.");

        await Assert.That(NearDuplicates.Jaccard(a, b)).IsEqualTo(1.0);
    }

    [Test]
    public async Task Jaccard_partialOverlap_isSharedOverUnion()
    {
        // a: {a b c, b c d}, b: {b c d, c d e} → 1 shared of 3.
        var a = NearDuplicates.Shingles("a b c d");
        var b = NearDuplicates.Shingles("b c d e");

        await Assert.That(NearDuplicates.Jaccard(a, b)).IsEqualTo(1.0 / 3);
    }

    [Test]
    public async Task Jaccard_emptySide_isZero()
    {
        await Assert.That(NearDuplicates.Jaccard([], NearDuplicates.Shingles("a b c"))).IsEqualTo(0.0);
        await Assert.That(NearDuplicates.Jaccard([], [])).IsEqualTo(0.0);
    }
}
