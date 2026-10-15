using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using RagAssistant.Web.Bff;
using RagAssistant.Web.Tests.Fixtures;

namespace RagAssistant.Web.Tests;

[ClassDataSource<WebAppFactory>(Shared = SharedType.PerTestSession)]
public partial class BffAuthTests(WebAppFactory factory)
{
    [Test]
    [Arguments("/")]
    [Arguments("/chat?x=1")]
    [Arguments("/bff/login")]
    public async Task Page_anonymous_redirects_to_the_keycloak_authorization_endpoint_with_pkce(string path)
    {
        var client = factory.CreateBrowser();

        var response = await client.GetAsync(new Uri(path, UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        var location = response.Headers.Location!.ToString();
        await Assert.That(location).StartsWith(WebAppFactory.AuthorizationEndpoint);
        await Assert.That(location).Contains("response_type=code");
        await Assert.That(location).Contains("client_id=ragassistant-web");
        await Assert.That(location).Contains("code_challenge_method=S256");
        await Assert.That(location).Contains("code_challenge=");
    }

    [Test]
    [Arguments(null, "/")]
    [Arguments("", "/")]
    [Arguments("/chat", "/chat")]
    [Arguments("/documents?page=2", "/documents?page=2")]
    [Arguments("//evil.example", "/")]
    [Arguments("/\\evil.example", "/")]
    [Arguments("https://evil.example/x", "/")]
    [Arguments("evil", "/")]
    public async Task LocalUrlOrRoot_only_keeps_local_paths(string? returnUrl, string expected)
    {
        await Assert.That(BffAuthExtensions.LocalUrlOrRoot(returnUrl)).IsEqualTo(expected);
    }

    [Test]
    public async Task Session_with_expired_access_token_is_rejected_for_api_and_pages()
    {
        var client = await factory.CreateSignedInAsync(Persona.Maintainer, accessTokenLifetimeSeconds: -60);

        var api = await client.GetAsync(new Uri("/api/echo/x", UriKind.Relative));
        var page = await client.GetAsync(new Uri("/", UriKind.Relative));

        await Assert.That(api.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(page.Headers.Location!.ToString()).StartsWith(WebAppFactory.AuthorizationEndpoint);
    }

    [Test]
    public async Task Session_cookie_is_http_only_and_same_site_lax()
    {
        var client = factory.CreateBrowser();

        var response = await client.GetAsync(new Uri("/test/sign-in?persona=KnowledgeSeeker&lifetime=3600", UriKind.Relative));

        var cookie = response.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("ragassistant.session=", StringComparison.Ordinal));
        await Assert.That(cookie.ToLowerInvariant()).Contains("httponly");
        await Assert.That(cookie.ToLowerInvariant()).Contains("samesite=lax");
    }

    [Test]
    public async Task Pages_never_contain_tokens()
    {
        var client = await factory.CreateSignedInAsync(Persona.Maintainer);

        var html = await client.GetStringAsync(new Uri("/", UriKind.Relative));

        await Assert.That(html).DoesNotContain("access-token-");
        await Assert.That(html).DoesNotContain("id-token");
    }

    [Test]
    public async Task Logout_get_is_not_allowed()
    {
        var client = await factory.CreateSignedInAsync(Persona.KnowledgeSeeker);

        var response = await client.GetAsync(new Uri("/bff/logout", UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.MethodNotAllowed);
    }

    [Test]
    public async Task Logout_post_without_antiforgery_token_is_rejected()
    {
        var client = await factory.CreateSignedInAsync(Persona.KnowledgeSeeker);

        var response = await client.PostAsync(new Uri("/bff/logout", UriKind.Relative), new FormUrlEncodedContent([]));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Logout_post_with_antiforgery_token_clears_the_session_and_ends_the_keycloak_session()
    {
        var client = await factory.CreateSignedInAsync(Persona.KnowledgeSeeker);
        var page = await client.GetStringAsync(new Uri("/", UriKind.Relative));
        var token = AntiforgeryTokenPattern().Match(page).Groups[1].Value;
        await Assert.That(token).IsNotEmpty();

        var response = await client.PostAsync(
            new Uri("/bff/logout", UriKind.Relative),
            new FormUrlEncodedContent([new KeyValuePair<string, string>("__RequestVerificationToken", token)]));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        var location = response.Headers.Location!.ToString();
        await Assert.That(location).StartsWith(WebAppFactory.EndSessionEndpoint);
        await Assert.That(location).Contains("id_token_hint=id-token");
        var api = await client.GetAsync(new Uri("/api/echo/x", UriKind.Relative));
        await Assert.That(api.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Proxy_unreachable_upstream_returns_502_problem()
    {
        var closedPort = ClosedPort();
        using var deadApi = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("services:api:http:0", $"http://127.0.0.1:{closedPort}");
            // Windows takes about 2 s to refuse a loopback connection; keep the timeout well above that.
            builder.UseSetting("Bff:ProxyActivityTimeout", "00:00:30");
        });
        var client = deadApi.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        (await client.GetAsync(new Uri("/test/sign-in?persona=KnowledgeSeeker&lifetime=3600", UriKind.Relative))).EnsureSuccessStatusCode();

        var response = await client.GetAsync(new Uri("/api/echo/x", UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadGateway);
        var problem = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        await Assert.That(problem.GetProperty("code").GetString()).IsEqualTo("upstream.unavailable");
    }

    // A port that was just free and has no listener refuses connections immediately.
    private static int ClosedPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryTokenPattern();
}
