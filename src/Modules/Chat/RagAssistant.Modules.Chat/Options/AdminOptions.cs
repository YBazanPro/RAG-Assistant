namespace RagAssistant.Modules.Chat.Options;

/// <summary>Settings of the maintainers' request views (spec 017).</summary>
internal sealed class AdminOptions
{
    public const string Section = "Admin";

    /// <summary>Where a request's telemetry trace can be opened, with <c>{traceId}</c> standing for the trace id; no link when empty.</summary>
    public string? TraceUrlTemplate { get; set; }

    internal const string TraceIdPlaceholder = "{traceId}";

    public bool IsValid => string.IsNullOrWhiteSpace(TraceUrlTemplate)
        || (TraceUrlTemplate.Contains(TraceIdPlaceholder, StringComparison.Ordinal)
            && Uri.IsWellFormedUriString(TraceUrlTemplate.Replace(TraceIdPlaceholder, "0", StringComparison.Ordinal), UriKind.Absolute));

    public string? TraceUrl(string? traceId) =>
        string.IsNullOrWhiteSpace(TraceUrlTemplate) || string.IsNullOrEmpty(traceId)
            ? null
            : TraceUrlTemplate.Replace(TraceIdPlaceholder, traceId, StringComparison.Ordinal);
}
