using System.Net;
using RagAssistant.Integration.Tests.Fixtures;

namespace RagAssistant.Integration.Tests;

[ClassDataSource<RagAssistantAppFactory>(Shared = SharedType.PerTestSession)]
public sealed class HostSmokeTests(RagAssistantAppFactory factory)
{
    [Test]
    public async Task Health_endpoint_returns_healthy()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health", UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }
}
