namespace BentaGo.Core.Common;

/// <summary>
/// Result of a domain operation: either a value or one or more domain errors.
/// A failure carries every discovered error so callers (and lane scripts)
/// report all problems in one pass; nothing has happened when the result is a
/// failure — builders emit movements only on success.
/// </summary>
public sealed class Result<T>
{
    private readonly T? _value;

    private Result(T value)
    {
        _value = value;
        Errors = Array.Empty<DomainError>();
    }

    private Result(IReadOnlyList<DomainError> errors)
    {
        _value = default;
        Errors = errors;
    }

    public T Value =>
        IsSuccess
            ? _value!
            : throw new InvalidOperationException($"Result is not success: {Errors[0].Code}");

    public IReadOnlyList<DomainError> Errors { get; }

    public bool IsSuccess => Errors.Count == 0;

    public bool IsFailure => !IsSuccess;

    public static Result<T> Ok(T value) => new(value);

    public static Result<T> Fail(params DomainError[] errors) =>
        new((IReadOnlyList<DomainError>)errors);

    public static Result<T> Fail(IEnumerable<DomainError> errors) =>
        new((IReadOnlyList<DomainError>)errors.ToArray());

    public Result<R> Map<R>(Func<T, R> map) =>
        IsSuccess ? Result<R>.Ok(map(_value!)) : Result<R>.Fail(Errors);
}
