using Microsoft.Extensions.AI;
using RagAssistant.Modules.Chat.Answering;
using RagAssistant.Modules.Chat.Options;
using RagAssistant.SharedKernel.Contracts.Documents;

namespace RagAssistant.Modules.Chat.Tests;

// Spec 021, AC-1 to AC-3 and AC-6: the context budget.
internal sealed class PromptBudgetTests
{
    private static IReadOnlyList<ChatMessage> Build(IReadOnlyList<RetrievedChunk> chunks) =>
        PromptBuilder.Build("Who approves?", chunks, EvidenceOutcome.Sufficient, "INSUFFICIENT_EVIDENCE");

    private static RetrievedChunk Chunk(int rank, int chars) =>
        new(Guid.NewGuid(), Guid.NewGuid(), $"Doc {rank}", "en", Guid.NewGuid(), 1, [], rank, null, null, new string('x', chars), 1.0 - (rank / 100.0));

    [Test]
    public async Task Estimate_countsThreeCharactersPerTokenPlusAnOverheadPerMessage()
    {
        IReadOnlyList<ChatMessage> messages = [new(ChatRole.System, new string('a', 300)), new(ChatRole.User, new string('b', 31))];

        await Assert.That(PromptBudget.Estimate(messages)).IsEqualTo(100 + 8 + 11 + 8);
    }

    [Test]
    public async Task Fit_promptThatFits_keepsEveryPassage()
    {
        RetrievedChunk[] ranked = [.. Enumerable.Range(1, 8).Select(i => Chunk(i, 1600))];

        var fitted = PromptBudget.Fit(ranked, Build, contextWindowTokens: 8192, maxOutputTokens: 1000);

        await Assert.That(fitted.Sent).IsEquivalentTo(ranked);
        await Assert.That(fitted.Trimmed).IsEqualTo(0);
        await Assert.That(fitted.EstimatedTokens).IsEqualTo(PromptBudget.Estimate(Build(ranked)));
    }

    [Test]
    public async Task Fit_promptOverTheWindow_dropsTheLowestRankedUntilItFits()
    {
        RetrievedChunk[] ranked = [.. Enumerable.Range(1, 8).Select(i => Chunk(i, 1600))];

        var fitted = PromptBudget.Fit(ranked, Build, contextWindowTokens: 4096, maxOutputTokens: 1000);

        await Assert.That(fitted.Trimmed).IsGreaterThan(0);
        await Assert.That(fitted.Sent.Select(c => c.PassageId)).IsEquivalentTo(ranked.Take(8 - fitted.Trimmed).Select(c => c.PassageId));
        await Assert.That(fitted.EstimatedTokens).IsLessThanOrEqualTo(4096 - 1000);
        // One more passage would not have fitted.
        await Assert.That(PromptBudget.Estimate(Build([.. ranked.Take(fitted.Sent.Count + 1)]))).IsGreaterThan(4096 - 1000);
    }

    [Test]
    public async Task Fit_windowSmallerThanOnePassage_stillSendsTheBestOne()
    {
        RetrievedChunk[] ranked = [Chunk(1, 20_000), Chunk(2, 100)];

        var fitted = PromptBudget.Fit(ranked, Build, contextWindowTokens: 2048, maxOutputTokens: 1000);

        await Assert.That(fitted.Sent.Single().PassageId).IsEqualTo(ranked[0].PassageId);
        await Assert.That(fitted.Trimmed).IsEqualTo(1);
        await Assert.That(fitted.EstimatedTokens).IsGreaterThan(2048);
    }

    [Test]
    [Arguments(1500, 1000, false)]
    [Arguments(2024, 1000, false)]
    [Arguments(2025, 1000, true)]
    [Arguments(8192, 1000, true)]
    public async Task HasRoomForPrompt_needsMoreThanTheAnswerPlusTheMinimumPrompt(int window, int output, bool expected)
    {
        var options = new GenerationOptions { ContextWindowTokens = window, MaxOutputTokens = output };

        await Assert.That(options.HasRoomForPrompt).IsEqualTo(expected);
    }
}
