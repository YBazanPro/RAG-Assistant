using System.Net;
using System.Net.Http.Json;
using RagAssistant.Web.Tests.Fixtures;

namespace RagAssistant.Web.Tests;

[ClassDataSource<WebAppFactory>(Shared = SharedType.PerTestSession)]
public class BffProxyTests(WebAppFactory factory)
{
    [Test]
    public async Task Proxy_signed_in_forwards_path_unchanged_with_bearer_token_and_no_cookie()
    {
        var client = await factory.CreateSignedInAsync(Persona.ContentOwner);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/echo/y?z=1");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer forged-by-the-browser");

        var response = await client.SendAsync(request);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var echo = await response.Content.ReadFromJsonAsync<EchoedRequest>();
        await Assert.That(echo!.Path).IsEqualTo("/api/echo/y?z=1");
        await Assert.That(echo.Authorization).IsEqualTo("Bearer access-token-ContentOwner");
        await Assert.That(echo.Cookie).IsEmpty();
    }

    [Test]
    public async Task Proxy_anonymous_returns_401_problem_and_forwards_nothing()
    {
        var before = factory.Host.RequestCount;
        var client = factory.CreateBrowser();

        var response = await client.GetAsync(new Uri("/api/echo/x", UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        await Assert.That(problem.GetProperty("code").GetString()).IsEqualTo("auth.unauthenticated");
        await Assert.That(problem.TryGetProperty("traceId", out _)).IsTrue();
        await Assert.That(factory.Host.RequestCount).IsEqualTo(before);
    }

    [Test]
    [Arguments("POST")]
    [Arguments("PUT")]
    [Arguments("PATCH")]
    [Arguments("DELETE")]
    public async Task Proxy_unsafe_method_without_csrf_header_returns_403(string method)
    {
        var client = await factory.CreateSignedInAsync(Persona.KnowledgeSeeker);

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), "/api/echo/x"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        var problem = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        await Assert.That(problem.GetProperty("code").GetString()).IsEqualTo("bff.csrf.header_missing");
    }

    [Test]
    public async Task Proxy_unsafe_method_with_csrf_header_is_forwarded()
    {
        var client = await factory.CreateSignedInAsync(Persona.KnowledgeSeeker);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/echo/x");
        request.Headers.Add("X-Requested-With", "ragassistant");

        var response = await client.SendAsync(request);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Proxy_streams_events_without_buffering_and_cancels_upstream_on_disconnect()
    {
        factory.Host.ResetStreamSignal();
        var client = await factory.CreateSignedInAsync(Persona.KnowledgeSeeker);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        using var response = await client.GetAsync(new Uri("/api/stream", UriKind.Relative), HttpCompletionOption.ResponseHeadersRead, cts.Token);
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);
        var firstLine = await reader.ReadLineAsync(cts.Token);

        // The stub needs about 5 s to finish 20 events, so receiving one now proves nothing is buffered.
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("text/event-stream");
        await Assert.That(firstLine).IsEqualTo("data: 0");

        response.Dispose();
        var completed = await Task.WhenAny(factory.Host.StreamCancelled, Task.Delay(TimeSpan.FromSeconds(5)));
        await Assert.That(completed).IsEqualTo(factory.Host.StreamCancelled);
    }

    [Test]
    public async Task Proxy_passes_host_problem_details_through_unchanged()
    {
        var client = await factory.CreateSignedInAsync(Persona.KnowledgeSeeker);

        var response = await client.GetAsync(new Uri("/api/problem", UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        var problem = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        await Assert.That(problem.GetProperty("code").GetString()).IsEqualTo("stub.not_found");
    }

    [Test]
    public async Task Proxy_upstream_that_does_not_answer_in_time_returns_504_problem()
    {
        var client = await factory.CreateSignedInAsync(Persona.KnowledgeSeeker);

        var response = await client.GetAsync(new Uri("/api/hang", UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.GatewayTimeout);
        var problem = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        await Assert.That(problem.GetProperty("code").GetString()).IsEqualTo("upstream.timeout");
    }
}
