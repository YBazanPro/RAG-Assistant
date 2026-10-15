namespace RagAssistant.SharedKernel.Common;

public enum ErrorType
{
    Validation,
    NotFound,
    Conflict,
    Forbidden,
    PreconditionFailed,
    PreconditionRequired,

    /// <summary>A dependency (e.g. the embedding model) cannot serve the request right now; retrying later may succeed.</summary>
    Unavailable,
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

    public static Error Unavailable(string code, string message) => new(code, message, ErrorType.Unavailable);

    public static Error Failure(string code, string message) => new(code, message, ErrorType.Failure);
}
