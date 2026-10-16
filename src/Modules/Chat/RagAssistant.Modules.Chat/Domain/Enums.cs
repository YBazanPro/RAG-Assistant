namespace RagAssistant.Modules.Chat.Domain;

internal enum MessageRole
{
    User,
    Assistant,
}

internal enum AnswerStatus
{
    Pending,
    Complete,
    Partial,
    Refused,
    Failed,
    Cancelled,
}

internal enum RefusalReason
{
    NoEvidence,
    ModelDeclined,
}

internal enum FeedbackRating
{
    Helpful,
    NotHelpful,
}

internal enum IssueCategory
{
    Incorrect,
    UnsupportedBySources,
    WrongSource,
    Outdated,
    Other,
}

/// <summary>Why the follow-up rewrite was skipped in favour of the original question (spec 014).</summary>
internal enum RewriteFallback
{
    Timeout,
    ModelError,
    Invalid,
}

/// <summary>The wire names (spec 012: enums as lowercase strings).</summary>
internal static class ChatApiNames
{
    public static string ToApi(this MessageRole role) => role == MessageRole.User ? "user" : "assistant";

    public static string ToApi(this AnswerStatus status) => status.ToString().ToLowerInvariant();

    public static string ToApi(this RefusalReason reason) => reason == RefusalReason.NoEvidence ? "no_evidence" : "model_declined";

    public static string ToApi(this FeedbackRating rating) => rating == FeedbackRating.Helpful ? "helpful" : "not_helpful";

    public static bool TryParseRating(string? value, out FeedbackRating rating)
    {
        rating = FeedbackRating.Helpful;
        switch (value)
        {
            case "helpful":
                return true;
            case "not_helpful":
                rating = FeedbackRating.NotHelpful;
                return true;
            default:
                return false;
        }
    }

    public static string ToApi(this IssueCategory category) => category switch
    {
        IssueCategory.Incorrect => "incorrect",
        IssueCategory.UnsupportedBySources => "unsupported_by_sources",
        IssueCategory.WrongSource => "wrong_source",
        IssueCategory.Outdated => "outdated",
        _ => "other",
    };

    public static bool TryParseCategory(string? value, out IssueCategory category)
    {
        IssueCategory? parsed = value switch
        {
            "incorrect" => IssueCategory.Incorrect,
            "unsupported_by_sources" => IssueCategory.UnsupportedBySources,
            "wrong_source" => IssueCategory.WrongSource,
            "outdated" => IssueCategory.Outdated,
            "other" => IssueCategory.Other,
            _ => null,
        };
        category = parsed ?? IssueCategory.Other;
        return parsed is not null;
    }

    public static string ToApi(this RewriteFallback reason) => reason switch
    {
        RewriteFallback.Timeout => "timeout",
        RewriteFallback.ModelError => "model_error",
        _ => "invalid",
    };
}
