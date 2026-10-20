using Microsoft.Extensions.AI;
using RagAssistant.SharedKernel.Contracts.Documents;

namespace RagAssistant.Modules.Chat.Answering;

/// <param name="Sent">The passages that go into the prompt, best first; never empty when the input was not.</param>
/// <param name="Trimmed">How many of the lowest-ranked passages were left out.</param>
/// <param name="EstimatedTokens">The estimate for the prompt built from <paramref name="Sent"/>.</param>
internal sealed record BudgetedPassages(IReadOnlyList<RetrievedChunk> Sent, int Trimmed, int EstimatedTokens);

/// <summary>
/// Keeps the answer prompt inside the model's context window (spec 021). The estimate errs on the large side (3 characters per
/// token, French and code included, plus a per-message overhead): a prompt that is cut by the provider loses its rules silently,
/// one passage too few only lowers recall. Without a tokenizer the estimate is all there is; the trace records it next to the
/// provider's count.
/// </summary>
internal static class PromptBudget
{
    public const int CharsPerToken = 3;
    public const int PerMessageTokens = 8;

    public static int Estimate(IReadOnlyList<ChatMessage> messages) =>
        messages.Sum(m => ((m.Text.Length + CharsPerToken - 1) / CharsPerToken) + PerMessageTokens);

    /// <summary>Drops the lowest-ranked passage while the prompt and the answer do not fit; the best passage always stays.</summary>
    /// <param name="build">Builds the full prompt for a set of passages, exactly as it will be sent.</param>
    public static BudgetedPassages Fit(
        IReadOnlyList<RetrievedChunk> ranked, Func<IReadOnlyList<RetrievedChunk>, IReadOnlyList<ChatMessage>> build, int contextWindowTokens, int maxOutputTokens)
    {
        var budget = contextWindowTokens - maxOutputTokens;
        var count = ranked.Count;
        var estimate = Estimate(build(ranked));
        while (estimate > budget && count > 1)
        {
            count--;
            estimate = Estimate(build([.. ranked.Take(count)]));
        }

        return new BudgetedPassages(count == ranked.Count ? ranked : [.. ranked.Take(count)], ranked.Count - count, estimate);
    }
}
