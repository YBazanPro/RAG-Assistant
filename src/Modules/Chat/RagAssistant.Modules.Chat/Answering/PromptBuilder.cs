using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;
using RagAssistant.SharedKernel.Contracts.Documents;

namespace RagAssistant.Modules.Chat.Answering;

/// <summary>
/// The one place the answer prompt is built (CLAUDE.md, RAG conventions). Passages are numbered in rank order; the number is the
/// marker the model cites. The conversation history is never part of it (spec 012; follow-ups are spec 014).
/// Lines end with '\n' on every platform, so the prompt (and what the demo client parses) is the same everywhere.
/// </summary>
internal static class PromptBuilder
{
    /// <summary>Recorded in every trace; change it whenever the wording changes.</summary>
    public const string PromptVersion = "012.1";

    public const string LowConfidenceInstruction =
        "The evidence may be incomplete. Answer only what the passages support, and say what they do not cover.";

    public static IReadOnlyList<ChatMessage> Build(string question, IReadOnlyList<RetrievedChunk> chunks, EvidenceOutcome outcome, string sentinel)
    {
        string[] rules =
        [
            "You answer questions using only the numbered passages in the user's message.",
            "Rules:",
            "- Answer in English.",
            "- Use only facts stated in the passages. Do not add facts, assumptions or general knowledge.",
            "- Cite every claim with the number of its passage in square brackets, like [1]. Cite passages separately: write [1][2], not [1, 2].",
            "- Be concise, but keep every condition and exception the passages state.",
            "- Write plain prose. Short lists are allowed; do not use headings.",
            $"- If the passages do not answer the question, reply with exactly {sentinel} and nothing else.",
        ];
        var system = string.Join('\n', outcome == EvidenceOutcome.LowConfidence ? [.. rules, $"- {LowConfidenceInstruction}"] : rules);

        var user = new StringBuilder("Passages:\n\n");
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            user.Append(CultureInfo.InvariantCulture, $"[{i + 1}] {chunk.DocumentTitle}");
            if (chunk.SectionPath.Count > 0)
            {
                user.Append(" — ").Append(string.Join(" › ", chunk.SectionPath));
            }

            if (Pages(chunk) is { } pages)
            {
                user.Append(" (").Append(pages).Append(')');
            }

            user.Append('\n').Append(chunk.Text).Append("\n\n");
        }

        user.Append("Question: ").Append(question);
        return [new ChatMessage(ChatRole.System, system), new ChatMessage(ChatRole.User, user.ToString())];
    }

    private static string? Pages(RetrievedChunk chunk) => (chunk.PageStart, chunk.PageEnd) switch
    {
        ({ } start, { } end) when end != start => string.Create(CultureInfo.InvariantCulture, $"pages {start}–{end}"),
        ({ } start, _) => string.Create(CultureInfo.InvariantCulture, $"page {start}"),
        _ => null,
    };
}
