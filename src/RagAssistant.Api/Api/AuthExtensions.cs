using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RagAssistant.Security;

namespace RagAssistant.Api.Api;

internal static class AuthExtensions
{
    private const string Realm = "ragassistant";
    private const string Audience = "ragassistant-api";
    private const string RolesClaim = "roles";

    public static IServiceCollection AddApiAuthentication(this IServiceCollection services, IHostEnvironment environment)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddKeycloakJwtBearer("keycloak", Realm, JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.Audience = Audience;
                options.RequireHttpsMetadata = !environment.IsDevelopment();
            });

        // Separate registration so it runs after the Keycloak helper's own options setup.
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme).Configure(options =>
        {
            // Keep claim names exactly as issued ("sub", "roles"); the default inbound mapping renames both.
            options.MapInboundClaims = false;
            options.TokenValidationParameters.RoleClaimType = RolesClaim;
            // The 5-minute default would keep an expired token usable for minutes after the realm's 1-hour lifetime.
            options.TokenValidationParameters.ClockSkew = TimeSpan.FromMinutes(1);
            options.Events = new JwtBearerEvents
            {
                OnChallenge = WriteUnauthorizedAsync,
                OnForbidden = WriteForbiddenAsync,
            };
        });

        // The fallback applies to every endpoint without its own authorization metadata, so a new group cannot be anonymous by accident.
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(PolicyNames.KnowledgeSeeker, policy => policy.RequireRole(RoleNames.KnowledgeSeeker))
            .AddPolicy(PolicyNames.ContentOwner, policy => policy.RequireRole(RoleNames.ContentOwner))
            .AddPolicy(PolicyNames.Maintainer, policy => policy.RequireRole(RoleNames.Maintainer));

        return services;
    }

    private static async Task WriteUnauthorizedAsync(JwtBearerChallengeContext context)
    {
        context.HandleResponse();
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = context.AuthenticateFailure is null
            ? JwtBearerDefaults.AuthenticationScheme
            : $"{JwtBearerDefaults.AuthenticationScheme} error=\"invalid_token\"";

        await WriteProblemAsync(
            context.HttpContext, StatusCodes.Status401Unauthorized, "auth.unauthenticated", "Authentication is required.");
    }

    private static Task WriteForbiddenAsync(ForbiddenContext context) =>
        WriteProblemAsync(
            context.HttpContext, StatusCodes.Status403Forbidden, "auth.forbidden", "You do not have access to this resource.");

    private static async Task WriteProblemAsync(HttpContext httpContext, int status, string code, string detail)
    {
        var problemDetails = httpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
        await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Detail = detail,
                Extensions = { ["code"] = code },
            },
        });
    }
}
