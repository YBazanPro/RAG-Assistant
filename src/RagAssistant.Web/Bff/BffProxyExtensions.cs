using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Transforms;

namespace RagAssistant.Web.Bff;

internal static class BffProxyExtensions
{
    public const string CsrfHeaderName = "X-Requested-With";
    public const string CsrfHeaderValue = "ragassistant";

    private const string ClusterId = "api";
    private const string UploadClusterId = "api-upload";

    // A document upload is at most 100 MiB (spec 010) plus the multipart framing and form fields; the API enforces the exact limits.
    private const long DefaultUploadMaxBodyBytes = (100L * 1024 * 1024) + (64 * 1024);

    public static IServiceCollection AddBffProxy(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddReverseProxy()
            .LoadFromMemory(
                [
                    new RouteConfig
                    {
                        RouteId = "api",
                        ClusterId = ClusterId,
                        AuthorizationPolicy = BffAuthExtensions.ApiPolicy,
                        Match = new RouteMatch { Path = "/api/{**catch-all}" },
                    },
                    // The two upload endpoints get a larger body limit and a long timeout; everything else keeps the defaults.
                    // Both are more specific than the catch-all route, and POST-only, so reads of the same paths stay on it.
                    UploadRoute("api-upload-import", "/api/documents", configuration),
                    UploadRoute("api-upload-version", "/api/documents/{documentId:guid}/versions", configuration),
                ],
                [
                    new ClusterConfig
                    {
                        ClusterId = ClusterId,
                        // Resolved through Aspire service discovery (the AppHost's reference to the API).
                        Destinations = new Dictionary<string, DestinationConfig> { ["api"] = new() { Address = "https+http://api" } },
                        // Activity-based, so an SSE stream with heartbeats is never cut by it.
                        HttpRequest = new ForwarderRequestConfig { ActivityTimeout = configuration.GetValue("Bff:ProxyActivityTimeout", TimeSpan.FromMinutes(2)) },
                    },
                    new ClusterConfig
                    {
                        ClusterId = UploadClusterId,
                        Destinations = new Dictionary<string, DestinationConfig> { ["api"] = new() { Address = "https+http://api" } },
                        // Activity-based as well: a slow connection may take long, but it must keep sending bytes.
                        HttpRequest = new ForwarderRequestConfig { ActivityTimeout = configuration.GetValue("Bff:UploadActivityTimeout", TimeSpan.FromMinutes(15)) },
                    },
                ])
            .ConfigureHttpClient((_, handler) => handler.ConnectTimeout = TimeSpan.FromSeconds(10))
            .AddServiceDiscoveryDestinationResolver()
            .AddTransforms(builderContext => builderContext.AddRequestTransform(AttachAccessTokenAsync));

        return services;
    }

    private static RouteConfig UploadRoute(string routeId, string path, IConfiguration configuration) => new()
    {
        RouteId = routeId,
        ClusterId = UploadClusterId,
        AuthorizationPolicy = BffAuthExtensions.ApiPolicy,
        MaxRequestBodySize = configuration.GetValue("Bff:UploadMaxBodyBytes", DefaultUploadMaxBodyBytes),
        Match = new RouteMatch { Path = path, Methods = ["POST"] },
    };

    public static IEndpointRouteBuilder MapBffProxy(this IEndpointRouteBuilder app)
    {
        app.MapReverseProxy(pipeline =>
        {
            pipeline.Use(RequireCsrfHeaderAsync);
            pipeline.Use(MapGatewayErrorsAsync);
        });

        return app;
    }

    private static async ValueTask AttachAccessTokenAsync(RequestTransformContext context)
    {
        // Never forward the browser's session cookie or anything it sent as Authorization.
        context.ProxyRequest.Headers.Remove("Cookie");
        context.ProxyRequest.Headers.Authorization = null;

        var token = await context.HttpContext.GetTokenAsync(BffAuthExtensions.CookieScheme, "access_token");
        if (token is not null)
        {
            context.ProxyRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    // The cookie authenticates these calls, so a fixed custom header (which a cross-site form cannot add) is required.
    private static Task RequireCsrfHeaderAsync(HttpContext context, Func<Task> next)
    {
        var method = context.Request.Method;
        var unsafeMethod = HttpMethods.IsPost(method) || HttpMethods.IsPut(method)
            || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);

        if (unsafeMethod && context.Request.Headers[CsrfHeaderName] != CsrfHeaderValue)
        {
            return BffAuthExtensions.WriteProblemAsync(
                context, StatusCodes.Status403Forbidden, "bff.csrf.header_missing", $"The {CsrfHeaderName} header is required.");
        }

        return next();
    }

    // The forwarder wraps what the server threw while the request body was being read, so look through the whole chain.
    private static bool IsRequestTooLarge(Exception? exception) => exception switch
    {
        null => false,
        BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } => true,
        AggregateException aggregate => aggregate.InnerExceptions.Any(IsRequestTooLarge),
        _ => IsRequestTooLarge(exception.InnerException),
    };

    private static async Task MapGatewayErrorsAsync(HttpContext context, Func<Task> next)
    {
        await next();

        var error = context.GetForwarderErrorFeature();
        if (error is null || context.Response.HasStarted || context.RequestAborted.IsCancellationRequested)
        {
            return;
        }

        // The request body was larger than the route allows: the caller's mistake, not an outage of the service.
        if (IsRequestTooLarge(error.Exception))
        {
            await BffAuthExtensions.WriteProblemAsync(
                context, StatusCodes.Status413PayloadTooLarge, "bff.request_too_large", "The request body is too large.");
        }
        else if (error.Error == ForwarderError.RequestTimedOut)
        {
            await BffAuthExtensions.WriteProblemAsync(
                context, StatusCodes.Status504GatewayTimeout, "upstream.timeout", "The service did not respond in time.");
        }
        else
        {
            await BffAuthExtensions.WriteProblemAsync(
                context, StatusCodes.Status502BadGateway, "upstream.unavailable", "The service is unavailable.");
        }
    }
}
