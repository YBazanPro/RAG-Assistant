using System.Net;
using System.Net.Http.Json;
using RagAssistant.Integration.Tests.Fixtures;

namespace RagAssistant.Integration.Tests.Chat;

// Spec 012, AC-3 and AC-4: every chat endpoint needs the KnowledgeSeeker policy, and another user's conversation is a 404.
[ClassDataSource<RagAssistantAppFactory>(Shared = SharedType.PerTestSession)]
public sealed class ChatAuthTests(RagAssistantAppFactory factory)
{
    public static IEnumerable<Func<(string Method, string Url)>> Endpoints()
    {
        var id = Guid.NewGuid();
        yield return () => ("POST", "/api/chat/conversations");
        yield return () => ("GET", "/api/chat/conversations");
        yield return () => ("GET", $"/api/chat/conversations/{id}");
    }

    [Test]
    [MethodDataSource(nameof(Endpoints))]
    public async Task Endpoint_withoutToken_returns401((string Method, string Url) endpoint)
    {
        using var client = factory.CreateClientAs(Persona.Anonymous);

        using var response = await SendAsync(client, endpoint);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    [MethodDataSource(nameof(Endpoints))]
    public async Task Endpoint_tokenWithoutSeekerRole_returns403((string Method, string Url) endpoint)
    {
        using var client = factory.CreateClientAs(Persona.KnowledgeSeeker, TokenFlaw.NoRoles);

        using var response = await SendAsync(client, endpoint);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    [Arguments(Persona.KnowledgeSeeker)]
    [Arguments(Persona.Maintainer)]
    public async Task Get_anotherUsersConversation_returns404(Persona persona)
    {
        using var owner = factory.CreateClientAs(Persona.KnowledgeSeeker, subject: ChatRequests.NewUser());
        var id = await owner.CreateConversationAsync();
        using var other = factory.CreateClientAs(persona, subject: ChatRequests.NewUser());

        var (status, body) = await other.GetJsonAsync($"/api/chat/conversations/{id}");

        await Assert.That(status).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(body.GetProperty("code").GetString()).IsEqualTo("chat.conversation_not_found");
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, (string Method, string Url) endpoint) =>
        endpoint.Method == "GET"
            ? client.GetAsync(endpoint.Url)
            : client.PostAsJsonAsync(endpoint.Url, new { question = "Who approves a budget exception?" });
}
