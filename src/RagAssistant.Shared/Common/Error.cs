namespace RagAssistant.Shared.Common;

public enum ErrorType
{
    Validation,
    NotFound,
    Conflict,
    Forbidden,
    Failure,
}

/// <param name="Code">Stable machine-readable identifier, "area.reason" (e.g. "documents.not_found").</param>
/// <param name="Message">User-facing English text.</param>
/// <param name="Type">Decides the HTTP status.</param>
public sealed record Error(string Code, string Message, ErrorType Type)
{
    public static Error Validation(string code, string message) => new(code, message, ErrorType.Validation);

    public static Error NotFound(string code, string message) => new(code, message, ErrorType.NotFound);

    public static Error Conflict(string code, string message) => new(code, message, ErrorType.Conflict);

    public static Error Forbidden(string code, string message) => new(code, message, ErrorType.Forbidden);

    public static Error Failure(string code, string message) => new(code, message, ErrorType.Failure);
}
