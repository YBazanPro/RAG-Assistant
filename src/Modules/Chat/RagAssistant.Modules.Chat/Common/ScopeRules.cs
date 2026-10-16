namespace RagAssistant.Modules.Chat.Common;

/// <summary>Limits and normalization of a conversation's scope (spec 015).</summary>
internal static class ScopeRules
{
    public const int MaxDocuments = 25;

    /// <summary>The retrieval limit (spec 011).</summary>
    public const int MaxTags = 20;

    /// <summary>Trimmed, lowercased, de-duplicated, blanks dropped: the same spelling retrieval uses for tags.</summary>
    public static string[] NormalizeTags(IEnumerable<string?>? tags) =>
        [.. (tags ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal)];
}
