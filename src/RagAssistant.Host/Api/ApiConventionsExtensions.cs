using System.Diagnostics;
using Microsoft.AspNetCore.Routing;
using Scalar.AspNetCore;

namespace RagAssistant.Host.Api;

internal static class ApiConventionsExtensions
{
    public static IServiceCollection AddApiConventions(this IServiceCollection services)
    {
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
            context.ProblemDetails.Extensions["traceId"] =
                Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier);

        services.AddExceptionHandler<ApiExceptionHandler>();

        // Minimal APIs only throw on unreadable bodies in Development by default; always throw so the handler sets the code.
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);

        services.AddSingleton(TimeProvider.System);
        services.AddOpenApi();
        return services;
    }

    public static WebApplication UseApiConventions(this WebApplication app)
    {
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        return app;
    }

    public static WebApplication MapApiDocumentation(this WebApplication app)
    {
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
            app.MapScalarApiReference();
        }

        return app;
    }
}
