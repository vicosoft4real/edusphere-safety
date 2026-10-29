namespace EduSphere.Safety.Shared;
public sealed record Error(string Code, string Message);
public sealed class Result<T>
{
    public T? Value { get; }
    public Error? Error { get; }
    public bool IsSuccess => Error is null;
    private Result(T? value, Error? error) { Value = value; Error = error; }
    public static Result<T> Ok(T value) => new(value, null);
    public static Result<T> Fail(string code, string message) => new(default, new Error(code, message));
}
