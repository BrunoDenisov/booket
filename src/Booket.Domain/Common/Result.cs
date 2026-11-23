namespace Booket.Domain.Common;

/// <summary>Category of a failure. The API layer maps each category to an HTTP status code.</summary>
public enum ErrorType
{
    Validation,
    NotFound,
    Conflict,
    Forbidden,
    Unauthorized,
    /// <summary>The request is well-formed but violates a business rule (e.g. cancellation cutoff).</summary>
    BusinessRule,
}

/// <summary>An expected, typed failure. Exceptions are reserved for the unexpected.</summary>
public record Error(string Code, string Message, ErrorType Type)
{
    public static Error NotFound(string code, string message) => new(code, message, ErrorType.NotFound);
    public static Error Conflict(string code, string message) => new(code, message, ErrorType.Conflict);
    public static Error Forbidden(string code, string message) => new(code, message, ErrorType.Forbidden);
    public static Error Unauthorized(string code, string message) => new(code, message, ErrorType.Unauthorized);
    public static Error Rule(string code, string message) => new(code, message, ErrorType.BusinessRule);
    public static Error Validation(string code, string message) => new(code, message, ErrorType.Validation);
}

/// <summary>A validation failure carrying per-field messages.</summary>
public sealed record ValidationError(IReadOnlyDictionary<string, string[]> Errors)
    : Error("validation_failed", "One or more validation errors occurred.", ErrorType.Validation);

public class Result
{
    protected Result(Error? error) => Error = error;

    public Error? Error { get; }
    public bool IsSuccess => Error is null;
    public bool IsFailure => !IsSuccess;

    public static Result Success() => new(null);
    public static Result Failure(Error error) => new(error);
    public static Result<T> Success<T>(T value) => Result<T>.Ok(value);

    public static implicit operator Result(Error error) => Failure(error);
}

public sealed class Result<T> : Result
{
    private readonly T? _value;

    private Result(T? value, Error? error) : base(error) => _value = value;

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Cannot read the value of a failed result ({Error!.Code}).");

    public static Result<T> Ok(T value) => new(value, null);
    public static new Result<T> Failure(Error error) => new(default, error);

    public static implicit operator Result<T>(T value) => Ok(value);
    public static implicit operator Result<T>(Error error) => Failure(error);
}
