namespace RagAssistant.Shared.Common;

public enum ErrorType
{
    Validation,
    NotFound,
    Conflict,
    Forbidden,
    PreconditionFailed,
    PreconditionRequired,
    Failure,
}

/// <param name="Code">Stable machine-readable identifier, "area.reason" (e.g. "documents.not_found").</param>
/// <param name="Message">User-facing English text.</param>
/// <param name="Type">Decides the HTTP status.</param>
/// <param name="Extensions">Extra ProblemDetails members (camelCase names), e.g. the id of the conflicting resource.</param>
public sealed record Error(
    string Code,
    string Message,
    ErrorType Type,
    IReadOnlyDictionary<string, object?>? Extensions = null)
{
    public static Error Validation(string code, string message) => new(code, message, ErrorType.Validation);

    public static Error NotFound(string code, string message) => new(code, message, ErrorType.NotFound);

    public static Error Conflict(string code, string message, IReadOnlyDictionary<string, object?>? extensions = null) =>
        new(code, message, ErrorType.Conflict, extensions);

    public static Error Forbidden(string code, string message) => new(code, message, ErrorType.Forbidden);

    public static Error PreconditionFailed(string code, string message, IReadOnlyDictionary<string, object?>? extensions = null) =>
        new(code, message, ErrorType.PreconditionFailed, extensions);

    public static Error PreconditionRequired(string code, string message) => new(code, message, ErrorType.PreconditionRequired);

    public static Error Failure(string code, string message) => new(code, message, ErrorType.Failure);
}
