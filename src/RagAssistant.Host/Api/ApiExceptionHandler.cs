using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace RagAssistant.Host.Api;

// Client aborts never reach this handler: ExceptionHandlerMiddleware itself drops them with a Debug entry.
internal sealed partial class ApiExceptionHandler(
    IProblemDetailsService problemDetails,
    IHostEnvironment environment,
    ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ProblemDetails problem;
        if (exception is BadHttpRequestException bad)
        {
            LogBadRequest(logger, bad.StatusCode);
            var tooLarge = bad.StatusCode == StatusCodes.Status413PayloadTooLarge;
            problem = new ProblemDetails
            {
                Status = bad.StatusCode,
                Detail = tooLarge ? "The request body is too large." : "The request could not be read.",
                Extensions = { ["code"] = tooLarge ? "request.too_large" : "request.malformed" },
            };
        }
        else
        {
            LogUnhandled(logger, exception);
            problem = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Detail = environment.IsDevelopment() ? exception.Message : "An unexpected error occurred.",
                Extensions = { ["code"] = "server.error" },
            };
        }

        httpContext.Response.StatusCode = problem.Status!.Value;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Rejected a bad request with status {StatusCode}")]
    private static partial void LogBadRequest(ILogger logger, int statusCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception")]
    private static partial void LogUnhandled(ILogger logger, Exception exception);
}
