using System.Diagnostics;
using System.Text.Json;

namespace RagAssistant.Modules.Evaluation.Common;

internal static class EvaluationTelemetry
{
    public const string SourceName = "RagAssistant.Evaluation";

    public static readonly ActivitySource Source = new(SourceName);
}

/// <summary>
/// Reads report fields without binding the harness's types: the module stores the report as the harness wrote it and only needs a
/// handful of values out of it. Every reader returns null for a missing property or one of the wrong kind.
/// </summary>
internal static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static JsonElement? Property(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value
            : null;

    public static JsonElement? Object(JsonElement? element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.Object } value ? value : null;

    public static JsonElement? Array(JsonElement? element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.Array } value ? value : null;

    public static string? String(JsonElement? element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    public static int? Int(JsonElement? element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var n) ? n : null;

    public static double? Number(JsonElement? element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.Number } value ? value.GetDouble() : null;

    public static bool? Bool(JsonElement? element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.True or JsonValueKind.False } value ? value.GetBoolean() : null;

    public static DateTimeOffset? Date(JsonElement? element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.String } value && value.TryGetDateTimeOffset(out var date) ? date : null;

    /// <summary>A stored JSON column as an element the response can carry.</summary>
    public static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
