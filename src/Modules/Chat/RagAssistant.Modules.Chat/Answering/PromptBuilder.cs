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
    public const string PromptVersion = "014.1";

    /// <summary>Recorded in the trace of every rewrite call; change it whenever the rewrite wording changes.</summary>
    public const string RewritePromptVersion = "rw.014.1";

    public const string LowConfidenceInstruction =
        "The evidence may be incomplete. Answer only what the passages support, and say what they do not cover.";

    /// <param name="question">The question the passages were retrieved for: the standalone question of a follow-up.</param>
    /// <param name="originalWording">What the user typed, when that differs from <paramref name="question"/>.</param>
    public static IReadOnlyList<ChatMessage> Build(
        string question, IReadOnlyList<RetrievedChunk> chunks, EvidenceOutcome outcome, string sentinel, string? originalWording = null)
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
        if (originalWording is not null && originalWording != question)
        {
            user.Append("\nThe user wrote: ").Append(originalWording);
        }

        return [new ChatMessage(ChatRole.System, system), new ChatMessage(ChatRole.User, user.ToString())];
    }

    /// <summary>
    /// The follow-up rewrite prompt (spec 014): the history resolves references, nothing more. The reply format is imposed by the
    /// structured-output request (<see cref="RewriteResult"/>), not by this text.
    /// </summary>
    public static IReadOnlyList<ChatMessage> BuildRewrite(IReadOnlyList<Exchange> history, string question)
    {
        string[] rules =
        [
            "You rewrite the user's latest question into one standalone question for searching documents.",
            "Rules:",
            "- Use the conversation only to resolve references in the latest question, such as \"it\", \"that amount\" or \"they\".",
            "- Keep the user's intent and wording as far as possible. Do not answer the question.",
            "- Do not add facts from the earlier answers beyond what is needed to resolve a reference.",
            "- If the latest question already stands alone, or starts a new topic, return it unchanged.",
            "- Write the question in English.",
        ];

        var user = new StringBuilder("Conversation so far:\n\n");
        foreach (var exchange in history)
        {
            user.Append("Question: ").Append(exchange.Question).Append('\n');
            if (exchange.Answer is not null)
            {
                user.Append("Answer: ").Append(exchange.Answer).Append('\n');
            }

            user.Append('\n');
        }

        user.Append("Current question: ").Append(question);
        return [new ChatMessage(ChatRole.System, string.Join('\n', rules)), new ChatMessage(ChatRole.User, user.ToString())];
    }

    private static string? Pages(RetrievedChunk chunk) => (chunk.PageStart, chunk.PageEnd) switch
    {
        ({ } start, { } end) when end != start => string.Create(CultureInfo.InvariantCulture, $"pages {start}–{end}"),
        ({ } start, _) => string.Create(CultureInfo.InvariantCulture, $"page {start}"),
        _ => null,
    };
}

/// <summary>The structured reply of the rewrite call.</summary>
internal sealed record RewriteResult(string StandaloneQuestion);
