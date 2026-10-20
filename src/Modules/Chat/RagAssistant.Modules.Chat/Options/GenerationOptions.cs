using System.ComponentModel.DataAnnotations;

namespace RagAssistant.Modules.Chat.Options;

internal sealed class GenerationOptions
{
    public const string Section = "Chat:Generation";

    /// <summary>The smallest room the window must leave for rules, question and passages once the answer is reserved (spec 021).</summary>
    public const int MinPromptTokens = 1024;

    /// <summary>Local models load slowly the first time, hence the generous default.</summary>
    public TimeSpan FirstTokenTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromMinutes(2);

    [Range(1, int.MaxValue)]
    public int MaxOutputTokens { get; set; } = 1000;

    /// <summary>
    /// The model's context window (spec 021): the prompt plus <see cref="MaxOutputTokens"/> must fit in it, and the API sends it to
    /// Ollama as <c>num_ctx</c>, which otherwise truncates a longer prompt silently.
    /// </summary>
    public int ContextWindowTokens { get; set; } = 8192;

    /// <summary>The whole reply the model gives when the passages do not answer the question.</summary>
    [Required]
    [RegularExpression(@"^\S+$")]
    public string Sentinel { get; set; } = "INSUFFICIENT_EVIDENCE";

    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    public bool IsConsistent =>
        FirstTokenTimeout > TimeSpan.Zero && TotalTimeout > FirstTokenTimeout && HeartbeatInterval > TimeSpan.Zero;

    public bool HasRoomForPrompt => ContextWindowTokens > (long)MaxOutputTokens + MinPromptTokens;

    /// <summary>A pending answer older than this cannot still be running (spec 012, stale answers).</summary>
    public TimeSpan StaleAfter => TotalTimeout + TimeSpan.FromMinutes(1);
}
