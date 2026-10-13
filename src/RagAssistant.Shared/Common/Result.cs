namespace RagAssistant.Shared.Common;

public interface IFailureFactory<TSelf>
    where TSelf : Result
{
    static abstract TSelf FromValidation(IReadOnlyDictionary<string, string[]> fieldErrors);
}

public class Result : IFailureFactory<Result>
{
    public const string ValidationFailedCode = "validation.failed";

    private readonly Error? _error;

    protected Result(Error? error, IReadOnlyDictionary<string, string[]>? fieldErrors = null)
    {
        _error = error;
        FieldErrors = fieldErrors;
    }

    public bool IsSuccess => _error is null;

    public bool IsFailure => !IsSuccess;

    public Error Error => _error ?? throw new InvalidOperationException("A successful result has no error.");

    /// <summary>Field name (camelCase) to messages; set only on validation failures.</summary>
    public IReadOnlyDictionary<string, string[]>? FieldErrors { get; }

    internal static Error ValidationError { get; } =
        Error.Validation(ValidationFailedCode, "One or more validation errors occurred.");

    public static Result Success() => new(null);

    public static Result Failure(Error error) => new(error);

    public static Result<T> Success<T>(T value) => new(value, null, null);

    public static Result<T> Failure<T>(Error error) => new(default, error, null);

    public static Result FromValidation(IReadOnlyDictionary<string, string[]> fieldErrors) =>
        new(ValidationError, fieldErrors);
}

public sealed class Result<T> : Result, IFailureFactory<Result<T>>
{
    private readonly T? _value;

    internal Result(T? value, Error? error, IReadOnlyDictionary<string, string[]>? fieldErrors)
        : base(error, fieldErrors) => _value = value;

    public T Value => IsSuccess ? _value! : throw new InvalidOperationException("A failed result has no value.");

    static Result<T> IFailureFactory<Result<T>>.FromValidation(IReadOnlyDictionary<string, string[]> fieldErrors) =>
        new(default, ValidationError, fieldErrors);

    public static implicit operator Result<T>(T value) => Success(value);

    public static implicit operator Result<T>(Error error) => Failure<T>(error);
}
