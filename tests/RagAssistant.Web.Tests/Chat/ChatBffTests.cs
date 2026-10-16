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
}
