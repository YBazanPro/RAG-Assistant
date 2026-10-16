using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using RagAssistant.Integration.Tests.Fixtures;

namespace RagAssistant.Integration.Tests;

[ClassDataSource<RagAssistantAppFactory>(Shared = SharedType.PerTestSession)]
public sealed class AuthTests(RagAssistantAppFactory factory)
{
    private const string ProblemJson = "application/problem+json";

    [Test]
    [Arguments(Persona.Anonymous, "anonymous", HttpStatusCode.OK)]
    [Arguments(Persona.Anonymous, "authenticated", HttpStatusCode.Unauthorized)]
    [Arguments(Persona.Anonymous, "seeker", HttpStatusCode.Unauthorized)]
    [Arguments(Persona.Anonymous, "owner", HttpStatusCode.Unauthorized)]
    [Arguments(Persona.Anonymous, "maintainer", HttpStatusCode.Unauthorized)]
    [Arguments(Persona.KnowledgeSeeker, "anonymous", HttpStatusCode.OK)]
    [Arguments(Persona.KnowledgeSeeker, "authenticated", HttpStatusCode.OK)]
    [Arguments(Persona.KnowledgeSeeker, "seeker", HttpStatusCode.OK)]
    [Arguments(Persona.KnowledgeSeeker, "owner", HttpStatusCode.Forbidden)]
    [Arguments(Persona.KnowledgeSeeker, "maintainer", HttpStatusCode.Forbidden)]
    [Arguments(Persona.ContentOwner, "anonymous", HttpStatusCode.OK)]
    [Arguments(Persona.ContentOwner, "authenticated", HttpStatusCode.OK)]
    [Arguments(Persona.ContentOwner, "seeker", HttpStatusCode.OK)]
    [Arguments(Persona.ContentOwner, "owner", HttpStatusCode.OK)]
    [Arguments(Persona.ContentOwner, "maintainer", HttpStatusCode.Forbidden)]
    [Arguments(Persona.Maintainer, "anonymous", HttpStatusCode.OK)]
    [Arguments(Persona.Maintainer, "authenticated", HttpStatusCode.OK)]
    [Arguments(Persona.Maintainer, "seeker", HttpStatusCode.OK)]
    [Arguments(Persona.Maintainer, "owner", HttpStatusCode.OK)]
    [Arguments(Persona.Maintainer, "maintainer", HttpStatusCode.OK)]
    public async Task Endpoint_perPersona_returnsExpectedStatus(Persona persona, string endpoint, HttpStatusCode expected)
    {
        using var client = factory.CreateClientAs(persona);

        var response = await client.GetAsync(new Uri($"/api/_test/{endpoint}", UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(expected);
    }

    [Test]
    public async Task NoToken_protectedEndpoint_returns401ProblemWithChallenge()
    {
        using var client = factory.CreateClientAs(Persona.Anonymous);

        var response = await client.GetAsync(new Uri("/api/_test/seeker", UriKind.Relative));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo(ProblemJson);
        await Assert.That(body.GetProperty("code").GetString()).IsEqualTo("auth.unauthenticated");
        await Assert.That(body.GetProperty("traceId").GetString()).IsNotNull();
        await Assert.That(response.Headers.WwwAuthenticate.Any(h => h.Scheme == "Bearer")).IsTrue();
    }

    [Test]
    public async Task InsufficientRole_protectedEndpoint_returns403Problem()
    {
        using var client = factory.CreateClientAs(Persona.KnowledgeSeeker);

        var response = await client.GetAsync(new Uri("/api/_test/maintainer", UriKind.Relative));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo(ProblemJson);
        await Assert.That(body.GetProperty("code").GetString()).IsEqualTo("auth.forbidden");
        await Assert.That(body.GetProperty("traceId").GetString()).IsNotNull();
    }

    [Test]
    [Arguments(TokenFlaw.Expired)]
    [Arguments(TokenFlaw.JustExpired)]
    [Arguments(TokenFlaw.WrongKey)]
    [Arguments(TokenFlaw.WrongIssuer)]
    [Arguments(TokenFlaw.WrongAudience)]
    public async Task InvalidToken_protectedEndpoint_returns401Problem(TokenFlaw flaw)
    {
        using var client = factory.CreateClientAs(Persona.Maintainer, flaw);

        var response = await client.GetAsync(new Uri("/api/_test/authenticated", UriKind.Relative));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(body.GetProperty("code").GetString()).IsEqualTo("auth.unauthenticated");
    }

    [Test]
    [Arguments("Basic dXNlcjpwYXNz")]
    [Arguments("Bearer not-a-jwt")]
    public async Task MalformedAuthorizationHeader_returns401(string header)
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", header);

        var response = await client.GetAsync(new Uri("/api/_test/authenticated", UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task TokenWithoutRoles_isAuthenticatedButFailsRolePolicies()
    {
        using var client = factory.CreateClientAs(Persona.Maintainer, TokenFlaw.NoRoles);

        var authenticated = await client.GetAsync(new Uri("/api/_test/authenticated", UriKind.Relative));
        var seeker = await client.GetAsync(new Uri("/api/_test/seeker", UriKind.Relative));

        await Assert.That(authenticated.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(seeker.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Anonymous_healthAndDocs_inDevelopment_areServed()
    {
        using var client = factory.CreateClientAs(Persona.Anonymous);

        foreach (var path in new[] { "/health", "/alive", "/openapi/v1.json", "/scalar" })
        {
            var response = await client.GetAsync(new Uri(path, UriKind.Relative));
            await Assert.That(response.IsSuccessStatusCode).IsTrue();
        }
    }

    [Test]
    public async Task Anonymous_healthAndDocs_inProduction_return401()
    {
        using var production = factory.WithWebHostBuilder(b => b.UseEnvironment("Production"));
        using var client = production.CreateClientAs(Persona.Anonymous);

        foreach (var path in new[] { "/health", "/alive", "/openapi/v1.json", "/scalar" })
        {
            var response = await client.GetAsync(new Uri(path, UriKind.Relative));
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }
    }

    [Test]
    public async Task OpenApiDocument_declaresBearerSchemeAndSecuredOperations()
    {
        using var client = factory.CreateClientAs(Persona.Anonymous);

        var doc = await client.GetFromJsonAsync<JsonElement>(new Uri("/openapi/v1.json", UriKind.Relative));

        var scheme = doc.GetProperty("components").GetProperty("securitySchemes").GetProperty("Bearer");
        await Assert.That(scheme.GetProperty("type").GetString()).IsEqualTo("http");
        await Assert.That(scheme.GetProperty("scheme").GetString()).IsEqualTo("bearer");

        var seeker = doc.GetProperty("paths").GetProperty("/api/_test/seeker").GetProperty("get");
        await Assert.That(seeker.TryGetProperty("security", out _)).IsTrue();
    }
}
