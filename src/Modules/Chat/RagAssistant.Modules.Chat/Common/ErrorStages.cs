using RagAssistant.Modules.Chat.Domain;

namespace RagAssistant.Modules.Chat.Common;

/// <summary>
/// The pipeline stage an error belongs to (spec 017). One mapping serves the metric and the admin summary, so they cannot disagree.
/// </summary>
internal static class ErrorStages
{
    public const string Rewrite = "rewrite";
    public const string Retrieval = "retrieval";
    public const string Generation = "generation";
    public const string Persistence = "persistence";

    public static readonly string[] All = [Rewrite, Retrieval, Generation, Persistence];

    /// <summary>The stage of a stored <c>ErrorCode</c>; codes this version does not know count as persistence, the catch-all of the pipeline.</summary>
    public static string StageOf(string errorCode) => errorCode switch
    {
        _ when errorCode.StartsWith("retrieval.", StringComparison.Ordinal) => Retrieval,
        _ when errorCode.StartsWith("chat.model_", StringComparison.Ordinal) => Generation,
        _ when errorCode.StartsWith("rewrite.", StringComparison.Ordinal) => Rewrite,
        _ => Persistence,
    };

    /// <summary>
    /// A failed rewrite falls back to the original question and is never an <c>ErrorCode</c> (spec 014), so it is counted from the
    /// fallback reason as <c>rewrite.timeout</c>, <c>rewrite.model_error</c> or <c>rewrite.invalid</c>.
    /// </summary>
    public static string RewriteCode(RewriteFallback fallback) => $"rewrite.{fallback.ToApi()}";

    /// <summary>The errors of one request, as (stage, code) pairs: its final error, and its rewrite fallback.</summary>
    public static IEnumerable<(string Stage, string Code)> Of(RequestTrace trace)
    {
        if (trace.RewriteFallbackReason is { } fallback)
        {
            yield return (Rewrite, RewriteCode(fallback));
        }

        if (trace.ErrorCode is { } code)
        {
            yield return (StageOf(code), code);
        }
    }
}
