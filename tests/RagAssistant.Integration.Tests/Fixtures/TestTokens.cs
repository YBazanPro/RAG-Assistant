using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using RagAssistant.Shared.Common.Security;

namespace RagAssistant.Integration.Tests.Fixtures;

/// <summary>Mints tokens shaped like Keycloak's (issuer, audience, flat "roles" with composites expanded) signed with a test key.</summary>
public static class TestTokens
{
    public const string Issuer = "http://localhost:8180/realms/ragassistant";
    public const string Audience = "ragassistant-api";

    private static readonly RsaSecurityKey _signingKey = new(RSA.Create(2048)) { KeyId = "test-key" };
    private static readonly RsaSecurityKey _otherKey = new(RSA.Create(2048)) { KeyId = "other-key" };

    public static SecurityKey SigningKey => _signingKey;

    public static string[] RolesFor(Persona persona) => persona switch
    {
        Persona.KnowledgeSeeker => [RoleNames.KnowledgeSeeker],
        Persona.ContentOwner => [RoleNames.ContentOwner, RoleNames.KnowledgeSeeker],
        Persona.Maintainer => [RoleNames.Maintainer, RoleNames.ContentOwner, RoleNames.KnowledgeSeeker],
        _ => [],
    };

    public static string Create(Persona persona, TokenFlaw flaw = TokenFlaw.None)
    {
        var now = DateTime.UtcNow;
        var claims = new Dictionary<string, object>
        {
            ["sub"] = $"test-{persona}",
            ["preferred_username"] = persona.ToString(),
        };
        if (flaw != TokenFlaw.NoRoles)
        {
            claims["roles"] = RolesFor(persona);
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = flaw == TokenFlaw.WrongIssuer ? "http://localhost:8180/realms/other" : Issuer,
            Audience = flaw == TokenFlaw.WrongAudience ? "another-api" : Audience,
            Claims = claims,
            NotBefore = flaw == TokenFlaw.Expired ? now.AddHours(-2) : now.AddMinutes(-1),
            Expires = flaw == TokenFlaw.Expired ? now.AddHours(-1) : now.AddHours(1),
            SigningCredentials = new SigningCredentials(
                flaw == TokenFlaw.WrongKey ? _otherKey : _signingKey, SecurityAlgorithms.RsaSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    public static HttpClient CreateClientAs(
        this WebApplicationFactory<Program> factory, Persona persona, TokenFlaw flaw = TokenFlaw.None)
    {
        var client = factory.CreateClient();
        if (persona != Persona.Anonymous)
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", Create(persona, flaw));
        }

        return client;
    }
}
