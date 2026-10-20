namespace RagAssistant.Modules.Documents.Suggestions;

/// <summary>What a suggested question may be (spec 020), for generated and edited ones alike.</summary>
internal static class SuggestionRules
{
    public const int MaxQuestions = 3;
    public const int MaxLength = 200;

    /// <summary>Trimmed, inner whitespace collapsed, empty ones and case-insensitive duplicates removed, in order.</summary>
    public static string[] Normalize(IEnumerable<string?> questions) =>
        [.. questions
            .Select(q => string.Join(' ', (q ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
            .Where(q => q.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
}

/// <summary>The <c>suggestionsError</c> codes.</summary>
internal static class SuggestionErrorCodes
{
    public const string ModelUnavailable = "model_unavailable";
    public const string ModelTimeout = "model_timeout";
    public const string InvalidReply = "invalid_reply";
    public const string RetrievalUnavailable = "retrieval_unavailable";
    public const string Unexpected = "unexpected";

    /// <summary>The host stopped while the step ran; reprocessing the document runs it again.</summary>
    public const string Interrupted = "interrupted";
}
