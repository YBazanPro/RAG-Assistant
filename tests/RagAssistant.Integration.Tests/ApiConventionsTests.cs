using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RagAssistant.Integration.Tests.Fixtures;
using RagAssistant.Integration.Tests.Sample;

namespace RagAssistant.Integration.Tests;

[ClassDataSource<RagAssistantAppFactory>(Shared = SharedType.PerTestSession)]
public sealed class ApiConventionsTests(RagAssistantAppFactory factory)
{
    private const string ProblemJson = "application/problem+json";

    [Test]
    public async Task Echo_validRequest_returnsEchoedValue()
    {
        using var client = factory.CreateClientAs(Persona.KnowledgeSeeker);

        var response = await client.PostAsJsonAsync("/api/_test/echo", new { text = "hello" });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body.GetProperty("text").GetString()).IsEqualTo("hello");
    }

    [Test]
    public async Task Echo_invalidRequest_returnsValidationProblemAndSkipsHandler()
    {
        using var client = factory.CreateClientAs(Persona.KnowledgeSeeker);
        var tooLong = new string('x', 25);

        var response = await client.PostAsJsonAsync("/api/_test/echo", new { text = tooLong });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo(ProblemJson);
        await Assert.That(body.GetProperty("code").GetString()).IsEqualTo("validation.failed");
        await Assert.That(body.GetProperty("traceId").GetString()).IsNotNull();
        await Assert.That(body.GetProperty("errors").TryGetProperty("text", out _)).IsTrue();
        await Assert.That(EchoHandler.Handled.Contains(tooLong)).IsFalse();
    }

    [Test]
    public async Task NotFound_handlerFailure_returns404WithErrorCode()
    {
        using var client = factory.CreateClientAs(Persona.KnowledgeSeeker);

        var response = await client.GetAsync(new Uri("/api/_test/not-found", UriKind.Relative));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo(ProblemJson);
        await Assert.That(body.GetProperty("code").GetString()).IsEqualTo("sample.not_found");
    }

    [Test]
    public async Task Conflict_handlerFailure_returns409WithErrorCode()
    {
        using var client = factory.CreateClientAs(Persona.KnowledgeSeeker);

        var response = await client.GetAsync(new Uri("/api/_test/conflict", UriKind.Relative));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(body.GetProperty("code").GetString()).IsEqualTo("sample.conflict");
    }

    [Test]
    public async Task Throws_inProduction_returnsGenericProblemAndLogsOnce()
    {
        using var production = factory.WithWebHostBuilder(b => b.UseEnvironment("Production"));
        using var client = production.CreateClientAs(Persona.KnowledgeSeeker);

        var response = await client.GetAsync(new Uri("/api/_test/throws?message=boom-prod", UriKind.Relative));
        var text = await response.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(text).RootElement;

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(body.GetProperty("traceId").GetString()).IsNotNull();
        await Assert.That(text.Contains("boom-prod", StringComparison.Ordinal)).IsFalse();
        await Assert.That(ErrorsFor("boom-prod")).IsEqualTo(1);
    }

    [Test]
    public async Task Throws_inDevelopment_includesExceptionTextInDetail()
    {
        using var client = factory.CreateClientAs(Persona.KnowledgeSeeker);

        var response = await client.GetAsync(new Uri("/api/_test/throws?message=boom-dev", UriKind.Relative));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(body.GetProperty("detail").GetString()).Contains("boom-dev");
    }

    [Test]
    public async Task UnknownRoute_underApi_returns404Problem()
    {
        using var client = factory.CreateClientAs(Persona.KnowledgeSeeker);

        var response = await client.GetAsync(new Uri("/api/does-not-exist", UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo(ProblemJson);
    }

    [Test]
    public async Task WrongMethod_onKnownRoute_returns405Problem()
    {
        using var client = factory.CreateClientAs(Persona.KnowledgeSeeker);

        var response = await client.PostAsync(new Uri("/api/_test/not-found", UriKind.Relative), content: null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.MethodNotAllowed);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo(ProblemJson);
    }

    [Test]
    public async Task Echo_malformedJson_returns400WithMalformedCode()
    {
        using var client = factory.CreateClientAs(Persona.KnowledgeSeeker);
        using var content = new StringContent("{ not json", Encoding.UTF8, "application/json");

        var response = await client.PostAsync(new Uri("/api/_test/echo", UriKind.Relative), content);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo(ProblemJson);
        await Assert.That(body.GetProperty("code").GetString()).IsEqualTo("request.malformed");
    }

    [Test]
    public async Task Echo_unsupportedContentType_returns415Problem()
    {
        using var client = factory.CreateClientAs(Persona.KnowledgeSeeker);
        using var content = new StringContent("hello", Encoding.UTF8, "text/plain");

        var response = await client.PostAsync(new Uri("/api/_test/echo", UriKind.Relative), content);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.UnsupportedMediaType);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo(ProblemJson);
    }

    [Test]
    public async Task Slow_clientAborts_logsNoErrorAndServerStaysHealthy()
    {
        using var client = factory.CreateClientAs(Persona.KnowledgeSeeker);
        using var cts = new CancellationTokenSource();

        var request = client.GetAsync(new Uri("/api/_test/slow", UriKind.Relative), cts.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        await cts.CancelAsync();
        await Assert.That(async () => await request).Throws<OperationCanceledException>();

        var waited = Stopwatch.StartNew();
        while (waited.Elapsed < TimeSpan.FromSeconds(5) && !AbortLogged())
        {
            await Task.Delay(50);
        }

        var health = await client.GetAsync(new Uri("/health", UriKind.Relative));

        await Assert.That(AbortLogged()).IsTrue();
        await Assert.That(factory.Logs.Entries.Any(e =>
            e.Level >= LogLevel.Error && e.Exception is OperationCanceledException)).IsFalse();
        await Assert.That(health.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Startup_mapsEndpointGroupsUnderApiAndKeepsHealthEndpoints()
    {
        using var client = factory.CreateClientAs(Persona.KnowledgeSeeker);

        var sample = await client.GetAsync(new Uri("/api/_test/conflict", UriKind.Relative));
        var health = await client.GetAsync(new Uri("/health", UriKind.Relative));
        var alive = await client.GetAsync(new Uri("/alive", UriKind.Relative));

        await Assert.That(sample.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(health.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(alive.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Root_noPlaceholder_returns404Problem()
    {
        using var client = factory.CreateClientAs(Persona.KnowledgeSeeker);

        var response = await client.GetAsync(new Uri("/", UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo(ProblemJson);
    }

    [Test]
    public async Task ApiDocs_inDevelopment_areServed()
    {
        using var client = factory.CreateClientAs(Persona.KnowledgeSeeker);

        var openApi = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));
        var scalar = await client.GetAsync(new Uri("/scalar", UriKind.Relative));

        await Assert.That(openApi.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(scalar.IsSuccessStatusCode).IsTrue();
    }

    [Test]
    public async Task ApiDocs_inProduction_return404()
    {
        using var production = factory.WithWebHostBuilder(b => b.UseEnvironment("Production"));
        using var client = production.CreateClientAs(Persona.KnowledgeSeeker);

        var openApi = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));
        var scalar = await client.GetAsync(new Uri("/scalar", UriKind.Relative));

        await Assert.That(openApi.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(scalar.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task TimeProvider_resolved_isSystemTimeProvider()
    {
        var provider = factory.Services.GetRequiredService<TimeProvider>();

        await Assert.That(provider).IsSameReferenceAs(TimeProvider.System);
    }

    private bool AbortLogged() =>
        factory.Logs.Entries.Any(e => e.Level == LogLevel.Debug && e.Message == "The request was aborted by the client.");

    private int ErrorsFor(string marker) =>
        factory.Logs.Entries.Count(e => e.Level == LogLevel.Error && e.Exception?.Message == marker);
}
