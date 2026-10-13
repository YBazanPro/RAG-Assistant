using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using RagAssistant.Web.Client.Security;

namespace RagAssistant.Web.Bff;

internal static class BffAuthExtensions
{
    public const string CookieScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    public const string OidcScheme = OpenIdConnectDefaults.AuthenticationScheme;
    public const string ApiPolicy = "BffApi";

    private const string Realm = "ragassistant";
    private const string ClientId = "ragassistant-web";
    private const string SessionCookieName = "ragassistant.session";

    public static IServiceCollection AddBffAuthentication(this IServiceCollection services, IWebHostEnvironment environment)
    {
        services.AddAuthentication(options =>
            {
                options.DefaultScheme = CookieScheme;
                options.DefaultChallengeScheme = OidcScheme;
            })
            .AddCookie(CookieScheme, options =>
            {
                options.Cookie.Name = SessionCookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
                // The access token lifetime (1 hour, from the realm) is the real limit: see OnValidatePrincipal.
                options.ExpireTimeSpan = TimeSpan.FromHours(1);
                options.SlidingExpiration = false;
                options.Events = new CookieAuthenticationEvents
                {
                    OnValidatePrincipal = RejectExpiredAccessTokenAsync,
                    OnRedirectToLogin = context => RespondAsync(context, StatusCodes.Status401Unauthorized),
                    OnRedirectToAccessDenied = context => RespondAsync(context, StatusCodes.Status403Forbidden),
                };
            })
            .AddKeycloakOpenIdConnect("keycloak", Realm, OidcScheme, options =>
            {
                options.ClientId = ClientId;
                options.ResponseType = "code";
                options.UsePkce = true;
                options.SaveTokens = true;
                options.SignInScheme = CookieScheme;
                options.RequireHttpsMetadata = !environment.IsDevelopment();
                // Keep claim names exactly as issued ("sub", "name", "roles").
                options.MapInboundClaims = false;
                options.GetClaimsFromUserInfoEndpoint = false;
                options.TokenValidationParameters.NameClaimType = "name";
                options.TokenValidationParameters.RoleClaimType = "roles";
                options.Scope.Clear();
                options.Scope.Add("openid");
                options.Scope.Add("profile");
            });

        services.AddOptions<OpenIdConnectOptions>(OidcScheme).Configure<IConfiguration>((options, configuration) =>
            options.ClientSecret = configuration["Keycloak:ClientSecret"]);

        return services;
    }

    public static IServiceCollection AddBffAuthorization(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(ApiPolicy, policy => policy.AddAuthenticationSchemes(CookieScheme).RequireAuthenticatedUser());

        services.AddUiAuthorization();
        return services;
    }

    public static IEndpointRouteBuilder MapBffEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/bff/login", (HttpContext context, string? returnUrl) =>
            {
                var target = LocalUrlOrRoot(returnUrl);
                return context.User.Identity?.IsAuthenticated == true
                    ? Results.LocalRedirect(target)
                    : Results.Challenge(new AuthenticationProperties { RedirectUri = target }, [OidcScheme]);
            })
            .AllowAnonymous();

        // Sign-out changes state, so it is POST only; say so explicitly because the Blazor router would otherwise answer 404.
        app.MapMethods("/bff/logout", ["GET", "HEAD"], (HttpContext context) =>
        {
            context.Response.Headers.Allow = "POST";
            return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
        });

        app.MapPost("/bff/logout", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.BadRequest();
            }

            return Results.SignOut(new AuthenticationProperties { RedirectUri = "/" }, [CookieScheme, OidcScheme]);
        });

        return app;
    }

    internal static string LocalUrlOrRoot(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl)
        && returnUrl[0] == '/'
        && (returnUrl.Length == 1 || (returnUrl[1] != '/' && returnUrl[1] != '\\'))
            ? returnUrl
            : "/";

    private static async Task RejectExpiredAccessTokenAsync(CookieValidatePrincipalContext context)
    {
        var expiresAt = context.Properties.GetTokenValue("expires_at");
        var time = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>();
        if (expiresAt is null
            || !DateTimeOffset.TryParse(expiresAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expiry)
            || expiry <= time.GetUtcNow())
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieScheme);
        }
    }

    // API calls get a status code (an XHR cannot follow a redirect to a login page); page navigations get the normal behavior.
    private static Task RespondAsync(RedirectContext<CookieAuthenticationOptions> context, int status)
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            return WriteProblemAsync(
                context.HttpContext,
                status,
                status == StatusCodes.Status401Unauthorized ? "auth.unauthenticated" : "auth.forbidden",
                status == StatusCodes.Status401Unauthorized ? "Authentication is required." : "You do not have access to this resource.");
        }

        if (status == StatusCodes.Status403Forbidden)
        {
            // The status code pages middleware re-executes the access denied page and keeps the 403.
            context.Response.StatusCode = status;
            return Task.CompletedTask;
        }

        var returnUrl = Uri.EscapeDataString(context.Request.Path + context.Request.QueryString);
        context.Response.Redirect($"/bff/login?returnUrl={returnUrl}");
        return Task.CompletedTask;
    }

    internal static async Task WriteProblemAsync(HttpContext httpContext, int status, string code, string detail)
    {
        httpContext.Response.StatusCode = status;
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

    internal static string TraceId(HttpContext httpContext) =>
        Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier;
}

