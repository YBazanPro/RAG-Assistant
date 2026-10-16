using System.Net.Http.Json;
using RagAssistant.Web.Tests.Fixtures;

namespace RagAssistant.Web.Tests.Chat;

// Spec 013, AC-18 (prerender) and AC-19 (the answer POST streams through the BFF unbuffered).
[ClassDataSource<WebAppFactory>(Shared = SharedType.PerTestSession)]
public class ChatBffTests(WebAppFactory factory)
{
    // AC-18
    [Test]
    public async Task ChatPage_prerender_rendersTheShellAndLoadingStateWithoutChatCalls()
    {
        var id = Guid.NewGuid();
        var client = await factory.CreateSignedInAsync(Persona.KnowledgeSeeker);

        var html = await client.GetStringAsync(new Uri($"/chat/{id}", UriKind.Relative));

        await Assert.That(html).Contains("Loading chat");
        await Assert.That(html).Contains("<title>Chat · RagAssistant</title>");
        await Assert.That(factory.Api.Received($"/api/chat/conversations/{id}")).IsFalse();
        await Assert.That(factory.Api.Received("/api/chat/conversations")).IsFalse();
    }

    // AC-19
    [Test]
    public async Task AnswerPost_streamsThroughTheBffBeforeTheUpstreamFinishes()
    {
        var id = Guid.NewGuid();
        var client = await factory.CreateSignedInAsync(Persona.KnowledgeSeeker);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/chat/conversations/{id}/messages", UriKind.Relative))
        {
            Content = JsonContent.Create(new { question = "What is the limit?" }),
        };
        request.Headers.Add("X-Requested-With", "ragassistant");
        request.Headers.Accept.ParseAdd("text/event-stream");

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        // The upstream holds its last event until released, so reading the first one now proves nothing is buffered.
        var first = await reader.ReadLineAsync(cts.Token);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("text/event-stream");
        await Assert.That(first).IsEqualTo("event: question.accepted");

        factory.Api.ReleaseChat(id);
        string? line;
        var sawCompleted = false;
        while ((line = await reader.ReadLineAsync(cts.Token)) is not null)
        {
            sawCompleted |= line == "event: answer.completed";
        }

        await Assert.That(sawCompleted).IsTrue();
    }
}
