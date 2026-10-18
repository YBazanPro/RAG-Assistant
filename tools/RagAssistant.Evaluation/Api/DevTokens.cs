using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace RagAssistant.Evaluation.Api;

/// <summary>
/// Access tokens for the dev users through the realm's public password-grant client (<c>ragassistant-dev</c>, spec 004). Tokens
/// are cached until a minute before they expire; a run outlasts one token, so expiry is checked on every request.
/// </summary>
internal sealed class DevTokens(HttpClient http, IOptions<EvaluationOptions> options, TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset ExpiresAt)> _cache = new(StringComparer.Ordinal);

    public async Task<string> GetAsync(string user, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(user, out var cached) && cached.ExpiresAt > clock.GetUtcNow())
        {
            return cached.Token;
        }

        var settings = options.Value;
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = settings.ClientId,
            ["username"] = user,
            ["password"] = PasswordOf(user, settings),
        });
        using var response = await http.PostAsync(settings.TokenEndpoint, form, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HarnessException(
                $"Keycloak refused a token for '{user}' ({(int)response.StatusCode}). Is the AppHost running, and does {settings.TokenEndpoint} answer?");
        }

        var body = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken)
            ?? throw new HarnessException("Keycloak returned an empty token response.");
        _cache[user] = (body.AccessToken, clock.GetUtcNow().AddSeconds(Math.Max(body.ExpiresIn - 60, 0)));
        return body.AccessToken;
    }

    private static string PasswordOf(string user, EvaluationOptions settings)
    {
        var configured = user == settings.OwnerUser ? settings.OwnerPassword
            : user == settings.MaintainerUser ? settings.MaintainerPassword
            : null;
        if (!string.IsNullOrEmpty(configured))
        {
            return configured;
        }

        // Like the Keycloak smoke test: the committed dev realm is the source of truth for the test users.
        using var realm = JsonDocument.Parse(File.ReadAllText(RepositoryPaths.Resolve(settings.RealmFile)));
        foreach (var entry in realm.RootElement.GetProperty("users").EnumerateArray())
        {
            if (entry.GetProperty("username").GetString() == user)
            {
                return entry.GetProperty("credentials")[0].GetProperty("value").GetString()
                    ?? throw new HarnessException($"The realm file has no password for '{user}'.");
            }
        }

        throw new HarnessException($"User '{user}' is not in the realm file; set Evaluation:OwnerPassword or MaintainerPassword.");
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
