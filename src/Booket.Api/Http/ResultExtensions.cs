using Booket.Domain.Common;

namespace Booket.Api.Http;

/// <summary>Maps application results to HTTP responses; every failure becomes RFC 9457 ProblemDetails.</summary>
public static class ResultExtensions
{
    public static IResult ToOk<T>(this Result<T> result) =>
        result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error!.ToProblem();

    public static IResult ToNoContent<T>(this Result<T> result) =>
        result.IsSuccess ? TypedResults.NoContent() : result.Error!.ToProblem();

    public static IResult ToCreated<T>(this Result<T> result, Func<T, string> location) =>
        result.IsSuccess ? TypedResults.Created(location(result.Value), result.Value) : result.Error!.ToProblem();

    public static IResult ToProblem(this Error error)
    {
        var extensions = new Dictionary<string, object?> { ["code"] = error.Code };

        if (error is ValidationError validation)
        {
            return TypedResults.ValidationProblem(
                validation.Errors.ToDictionary(e => e.Key, e => e.Value),
                detail: error.Message,
                extensions: extensions);
        }

        return TypedResults.Problem(
            detail: error.Message,
            statusCode: StatusCode(error.Type),
            extensions: extensions);
    }

    public static int StatusCode(ErrorType type) => type switch
    {
        ErrorType.Validation => StatusCodes.Status400BadRequest,
        ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorType.Forbidden => StatusCodes.Status403Forbidden,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        ErrorType.BusinessRule => StatusCodes.Status422UnprocessableEntity,
        _ => StatusCodes.Status500InternalServerError,
    };
}
