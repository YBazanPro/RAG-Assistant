using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using TUnit.Core.Interfaces;

namespace RagAssistant.Web.Tests.Fixtures;

public sealed class WebAppFactory : WebApplicationFactory<Program>, IAsyncInitializer
{
    public const string Authority = "http://keycloak.test/realms/ragassistant";
    public const string AuthorizationEndpoint = Authority + "/protocol/openid-connect/auth";
    public const string EndSessionEndpoint = Authority + "/protocol/openid-connect/logout";
    public const string ClientSecret = "test-secret";

    [ClassDataSource<StubApi>(Shared = SharedType.PerTestSession)]
    public required StubApi Api { get; init; }

    public Task InitializeAsync()
    {
        // Start the server now so the stub address is known to the app before the first request.
        _ = Server;
        return Task.CompletedTask;
    }

    // WebApplicationFactory records every client and derived factory in a plain List<T> and disposes them at the end. This
    // factory is shared by test classes running in parallel, and concurrent adds can leave a null slot, which then throws a
    // NullReferenceException from DisposeAsync at session teardown. So every add goes through this lock.
    private readonly Lock _registration = new();

    public new HttpClient CreateClient()
    {
        lock (_registration)
        {
            return base.CreateClient();
        }
    }

    public new HttpClient CreateClient(WebApplicationFactoryClientOptions options)
    {
        lock (_registration)
        {
            return base.CreateClient(options);
        }
    }

    public new WebApplicationFactory<Program> WithWebHostBuilder(Action<IWebHostBuilder> configuration)
    {
        lock (_registration)
        {
            return base.WithWebHostBuilder(configuration);
        }
    }

    public HttpClient CreateBrowser() =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    /// <summary>Signs a persona in through the real cookie handler and returns a client holding the session cookie.</summary>
    public async Task<HttpClient> CreateSignedInAsync(Persona persona, int accessTokenLifetimeSeconds = 3600)
    {
        var client = CreateBrowser();
        var response = await client.GetAsync(new Uri($"/test/sign-in?persona={persona}&lifetime={accessTokenLifetimeSeconds}", UriKind.Relative));
        response.EnsureSuccessStatusCode();
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Same keys the AppHost's references produce, so service discovery resolves the stub like it resolves the API.
        builder.UseSetting("services:api:http:0", Api.Url);
        builder.UseSetting("services:keycloak:http:0", "http://keycloak.test");
        builder.UseSetting("Keycloak:ClientSecret", ClientSecret);
        builder.UseSetting("Bff:ProxyActivityTimeout", "00:00:02");

        builder.ConfigureServices(services =>
        {
            // Configure (not PostConfigure): the framework's post-configure would otherwise build a discovery manager from the authority first.
            services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
            {
                options.Authority = null;
                options.MetadataAddress = null!;
                options.RequireHttpsMetadata = false;
                options.Configuration = new OpenIdConnectConfiguration
                {
                    Issuer = Authority,
                    AuthorizationEndpoint = AuthorizationEndpoint,
                    EndSessionEndpoint = EndSessionEndpoint,
                    TokenEndpoint = Authority + "/protocol/openid-connect/token",
                };
            });

            services.AddSingleton<IStartupFilter, TestSignInStartupFilter>();
        });
    }

    // Test-only middleware: GET /test/sign-in?persona=Maintainer&lifetime=3600 issues the same cookie a real sign-in would.
    private sealed class TestSignInStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    if (!context.Request.Path.Equals("/test/sign-in", StringComparison.Ordinal))
                    {
                        await nextMiddleware(context);
                        return;
                    }

                    var persona = Enum.Parse<Persona>(context.Request.Query["persona"]!);
                    var lifetime = int.Parse(context.Request.Query["lifetime"]!, System.Globalization.CultureInfo.InvariantCulture);

                    var claims = new List<Claim>
                    {
                        new("sub", $"user-{persona}"),
                        new("name", $"{persona} Dev"),
                    };
                    claims.AddRange(PersonaRoles.For(persona).Select(role => new Claim("roles", role)));

                    var identity = new ClaimsIdentity(claims, "test", nameType: "name", roleType: "roles");
                    var properties = new AuthenticationProperties();
                    properties.StoreTokens(
                    [
                        new AuthenticationToken { Name = "access_token", Value = $"access-token-{persona}" },
                        new AuthenticationToken { Name = "id_token", Value = "id-token" },
                        new AuthenticationToken
                        {
                            Name = "expires_at",
                            Value = DateTimeOffset.UtcNow.AddSeconds(lifetime).ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                        },
                    ]);

                    await context.SignInAsync("Cookies", new ClaimsPrincipal(identity), properties);
                    context.Response.StatusCode = StatusCodes.Status200OK;
                });

                next(app);
            };
    }
}
