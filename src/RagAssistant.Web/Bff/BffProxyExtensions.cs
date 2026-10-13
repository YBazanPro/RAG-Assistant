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

    private const string ClusterId = "host";

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
                ],
                [
                    new ClusterConfig
                    {
                        ClusterId = ClusterId,
                        // Resolved through Aspire service discovery (the AppHost's reference to the Host).
                        Destinations = new Dictionary<string, DestinationConfig> { ["host"] = new() { Address = "https+http://host" } },
                        // Activity-based, so an SSE stream with heartbeats is never cut by it.
                        HttpRequest = new ForwarderRequestConfig { ActivityTimeout = configuration.GetValue("Bff:ProxyActivityTimeout", TimeSpan.FromMinutes(2)) },
                    },
                ])
            .ConfigureHttpClient((_, handler) => handler.ConnectTimeout = TimeSpan.FromSeconds(10))
            .AddServiceDiscoveryDestinationResolver()
            .AddTransforms(builderContext => builderContext.AddRequestTransform(AttachAccessTokenAsync));

        return services;
    }

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

    private static async Task MapGatewayErrorsAsync(HttpContext context, Func<Task> next)
    {
        await next();

        var error = context.GetForwarderErrorFeature();
        if (error is null || context.Response.HasStarted || context.RequestAborted.IsCancellationRequested)
        {
            return;
        }

        if (error.Error == ForwarderError.RequestTimedOut)
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
