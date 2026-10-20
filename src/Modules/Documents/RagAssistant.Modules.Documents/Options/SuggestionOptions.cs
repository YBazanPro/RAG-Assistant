using System.ComponentModel.DataAnnotations;
using RagAssistant.Modules.Documents.Suggestions;

namespace RagAssistant.Modules.Documents.Options;

/// <summary>The suggested questions step after indexing (spec 020).</summary>
internal sealed class SuggestionOptions
{
    public const string Section = "Documents:Suggestions";

    /// <summary>Off: every indexed version is <c>skipped</c> and the chat model is never called. The API turns it off in demo mode.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How many questions the model is asked for; the retrieval check may keep fewer.</summary>
    [Range(1, SuggestionRules.MaxQuestions)]
    public int Questions { get; set; } = SuggestionRules.MaxQuestions;

    /// <summary>The start of the version's text the model reads; a small model loses the thread in a long prompt.</summary>
    [Range(500, 100_000)]
    public int MaxPromptCharacters { get; set; } = 6_000;

    /// <summary>The one chat call; on a CPU a small model needs tens of seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);
}
