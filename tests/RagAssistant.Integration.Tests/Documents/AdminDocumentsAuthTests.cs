using System.Net;
using RagAssistant.Integration.Tests.Fixtures;

namespace RagAssistant.Integration.Tests.Documents;

// Spec 017, AC-1: every admin route of the Documents module needs the Maintainer policy.
[ClassDataSource<RagAssistantAppFactory>(Shared = SharedType.PerTestSession)]
public sealed class AdminDocumentsAuthTests(RagAssistantAppFactory factory)
{
    public static IEnumerable<Func<string>> Routes()
    {
        yield return () => "/api/admin/documents/health";
        yield return () => "/api/admin/documents/failures";
        yield return () => "/api/admin/documents/private";
        yield return () => "/api/admin/documents/configuration";
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
    public async Task Route_asMaintainer_returns200(string url)
    {
        using var client = factory.CreateClientAs(Persona.Maintainer);

        using var response = await client.GetAsync(url);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }
}
