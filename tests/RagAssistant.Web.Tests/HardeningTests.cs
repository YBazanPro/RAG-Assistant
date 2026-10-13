using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using RagAssistant.Web.Tests.Fixtures;

namespace RagAssistant.Web.Tests;

[ClassDataSource<WebAppFactory>(Shared = SharedType.PerTestSession)]
public class HardeningTests(WebAppFactory factory)
{
    [Test]
    [Arguments("/")]
    [Arguments("/admin")]
    [Arguments("/api/echo/x")]
    public async Task Responses_carry_baseline_security_headers(string path)
    {
        var client = await factory.CreateSignedInAsync(Persona.KnowledgeSeeker);

        var response = await client.GetAsync(new Uri(path, UriKind.Relative));

        await Assert.That(response.Headers.GetValues("X-Content-Type-Options").Single()).IsEqualTo("nosniff");
        await Assert.That(response.Headers.GetValues("Referrer-Policy").Single()).IsEqualTo("same-origin");
        await Assert.That(response.Headers.GetValues("Content-Security-Policy").Single()).Contains("frame-ancestors 'none'");
    }

    [Test]
    public async Task Anonymous_challenge_redirects_also_carry_the_security_headers()
    {
        var response = await factory.CreateBrowser().GetAsync(new Uri("/", UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(response.Headers.Contains("X-Content-Type-Options")).IsTrue();
    }

    [Test]
    public async Task Unhandled_exception_in_production_shows_a_generic_static_error_page_without_details()
    {
        using var production = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureServices(services =>
            {
                services.AddHttpContextAccessor();
                services.AddSingleton<IClaimsTransformation, ThrowOncePerRequest>();
            });
        });
        var client = production.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = true,
        });
        (await client.GetAsync(new Uri("/test/sign-in?persona=KnowledgeSeeker&lifetime=3600", UriKind.Relative))).EnsureSuccessStatusCode();

        client.DefaultRequestHeaders.Add("X-Test-Throw", "1");

        var response = await client.GetAsync(new Uri("/", UriKind.Relative));
        var html = await response.Content.ReadAsStringAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(html).Contains("Something went wrong");
        await Assert.That(html).DoesNotContain("boom-secret-detail");
        await Assert.That(html).DoesNotContain("\"type\":\"webassembly\"");
    }

    // Throws inside the pipeline (after the exception handler), but only the first time for a request, so the
    // re-executed error page can render.
    private sealed class ThrowOncePerRequest(IHttpContextAccessor accessor) : IClaimsTransformation
    {
        public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
        {
            var context = accessor.HttpContext;
            if (context is not null && context.Request.Headers.ContainsKey("X-Test-Throw") && context.Items.TryAdd("test-thrown", true))
            {
                throw new InvalidOperationException("boom-secret-detail");
            }

            return Task.FromResult(principal);
        }
    }
}
