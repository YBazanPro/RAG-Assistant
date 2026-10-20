using System.Text.RegularExpressions;

namespace RagAssistant.Modules.Chat.Common;

/// <summary>The title of a conversation is its first question, cut to fit a list (spec 012).</summary>
internal static partial class ConversationTitle
{
    public const int MaxLength = 80;

    /// <summary>A title the owner chose (spec 020).</summary>
    public const int MaxRenamedLength = 200;

    public static string From(string question)
    {
        var collapsed = Whitespace().Replace(question.Trim(), " ");
        if (collapsed.Length <= MaxLength)
        {
            return collapsed;
        }

        // Leave room for the ellipsis and cut at the last space; a single overlong word is cut mid-word.
        var limit = MaxLength - 1;
        var cut = collapsed.LastIndexOf(' ', limit);
        var head = cut > 0 ? collapsed[..cut] : collapsed[..limit];
        return head.TrimEnd() + "…";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
