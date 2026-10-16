using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace RagAssistant.Integration.Tests.Chat;

/// <summary>Small helpers over the chat REST endpoints (spec 012).</summary>
internal static class ChatRequests
{
    public static string NewUser() => $"user-{Guid.NewGuid():N}";

    public static async Task<Guid> CreateConversationAsync(this HttpClient client)
    {
        using var response = await client.PostAsync("/api/chat/conversations", content: null);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetGuid();
    }

    public static async Task<(HttpStatusCode Status, JsonElement Body)> GetJsonAsync(this HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }
}
