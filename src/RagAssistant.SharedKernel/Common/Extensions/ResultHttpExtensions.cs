using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace RagAssistant.SharedKernel.Common.Extensions;

public static class ResultHttpExtensions
{
    public static IResult ToHttpResult(this Result result, Func<IResult>? onSuccess = null) =>
        result.IsSuccess ? (onSuccess?.Invoke() ?? TypedResults.NoContent()) : result.ToProblem();

    public static IResult ToHttpResult<T>(this Result<T> result, Func<T, IResult>? onSuccess = null) =>
        result.IsSuccess ? (onSuccess?.Invoke(result.Value) ?? TypedResults.Ok(result.Value)) : result.ToProblem();

    public static int ToStatusCode(this ErrorType type) => type switch
    {
        ErrorType.Validation => StatusCodes.Status400BadRequest,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        ErrorType.Forbidden => StatusCodes.Status403Forbidden,
        ErrorType.PreconditionFailed => StatusCodes.Status412PreconditionFailed,
        ErrorType.PreconditionRequired => StatusCodes.Status428PreconditionRequired,
        ErrorType.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError,
    };

    private static ProblemResult ToProblem(this Result result)
    {
        var error = result.Error;
        var status = error.Type.ToStatusCode();

        ProblemDetails problem = result.FieldErrors is { } fields
            ? new HttpValidationProblemDetails(fields.ToDictionary(f => f.Key, f => f.Value)) { Status = status }
            : new ProblemDetails { Status = status };

        problem.Detail = error.Message;
        problem.Extensions["code"] = error.Code;
        if (error.Extensions is { } extensions)
        {
            foreach (var (key, value) in extensions)
            {
                problem.Extensions[key] = value;
            }
        }

        return new ProblemResult(problem);
    }
}

/// <summary>Writes through <see cref="IProblemDetailsService"/> so every error response gets the same customization (traceId).</summary>
public sealed class ProblemResult(ProblemDetails problem) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        var service = httpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
        await service.WriteAsync(new ProblemDetailsContext { HttpContext = httpContext, ProblemDetails = problem });
    }
}
