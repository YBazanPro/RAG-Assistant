using System.Net;
using RagAssistant.Integration.Tests.Fixtures;

namespace RagAssistant.Integration.Tests.Chat;

// Spec 017, AC-1: every admin route needs the Maintainer policy.
[ClassDataSource<RagAssistantAppFactory>(Shared = SharedType.PerTestSession)]
public sealed class AdminChatAuthTests(RagAssistantAppFactory factory)
{
    public static IEnumerable<Func<string>> Routes()
    {
        yield return () => "/api/admin/chat/requests";
        yield return () => $"/api/admin/chat/requests/{Guid.NewGuid()}";
        yield return () => "/api/admin/chat/summary";
        yield return () => "/api/admin/chat/feedback";
        yield return () => "/api/admin/chat/configuration";
    }

    [Test]
    [MethodDataSource(nameof(Routes))]
    public async Task Route_withoutToken_returns401(string url)
    {
        using var client = factory.CreateClientAs(Persona.Anonymous);

        using var response = await client.GetAsync(url);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    [MethodDataSource(nameof(Routes))]
    public async Task Route_asSeekerOrOwner_returns403(string url)
    {
        foreach (var persona in new[] { Persona.KnowledgeSeeker, Persona.ContentOwner })
        {
            using var client = factory.CreateClientAs(persona);

            using var response = await client.GetAsync(url);

            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        }
    }

    [Test]
    [MethodDataSource(nameof(Routes))]
    public async Task Route_asMaintainer_isAllowed(string url)
    {
        using var client = factory.CreateClientAs(Persona.Maintainer);

        using var response = await client.GetAsync(url);

        // The detail route has no such request: 404 is the endpoint answering, not the policy refusing.
        var expected = url.Contains("/requests/", StringComparison.Ordinal) ? HttpStatusCode.NotFound : HttpStatusCode.OK;
        await Assert.That(response.StatusCode).IsEqualTo(expected);
    }
}
