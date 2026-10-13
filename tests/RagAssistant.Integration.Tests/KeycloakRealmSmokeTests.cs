using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using RagAssistant.Integration.Tests.Fixtures;

namespace RagAssistant.Integration.Tests;

// Catches mistakes in the realm file that test-key tokens cannot (spec 004, AC-11).
[ClassDataSource<KeycloakFixture>(Shared = SharedType.PerTestSession)]
public sealed class KeycloakRealmSmokeTests(KeycloakFixture keycloak)
{
    [Test]
    [Arguments("seeker", new[] { "knowledge-seeker" })]
    [Arguments("owner", new[] { "content-owner", "knowledge-seeker" })]
    [Arguments("maintainer", new[] { "maintainer", "content-owner", "knowledge-seeker" })]
    public async Task DevClient_passwordGrant_returnsTokenWithAudienceSubAndRoles(string username, string[] expectedRoles)
    {
        var password = ReadPasswordFromRealmFile(username);
        using var http = new HttpClient();
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = "ragassistant-dev",
            ["username"] = username,
            ["password"] = password,
        });

        var response = await http.PostAsync(
            new Uri($"{keycloak.BaseAddress.TrimEnd('/')}/realms/ragassistant/protocol/openid-connect/token"), form);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var token = new JsonWebTokenHandler().ReadJsonWebToken(body.GetProperty("access_token").GetString());

        var roles = token.GetPayloadValue<JsonElement>("roles").EnumerateArray().Select(r => r.GetString()!).Order().ToArray();
        await Assert.That(response.IsSuccessStatusCode).IsTrue();
        await Assert.That(token.Audiences).Contains("ragassistant-api");
        await Assert.That(string.IsNullOrEmpty(token.Subject)).IsFalse();
        await Assert.That(roles).IsEquivalentTo(expectedRoles.Order().ToArray());
    }

    private static string ReadPasswordFromRealmFile(string username)
    {
        using var realm = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, KeycloakFixture.RealmFileName)));
        return realm.RootElement.GetProperty("users").EnumerateArray()
            .Single(u => u.GetProperty("username").GetString() == username)
            .GetProperty("credentials")[0].GetProperty("value").GetString()!;
    }
}
