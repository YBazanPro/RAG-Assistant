using System.Net;
using System.Text;
using System.Text.Json;
using RagAssistant.Api.Retrieval;

namespace RagAssistant.Integration.Tests.Documents;

// Spec 019: the API's Text Embeddings Inference client (ADR 0010), against a fake TEI.
public sealed class TeiPassageRerankerTests
{
    private sealed class Tei(HttpStatusCode status, string reply) : HttpMessageHandler
    {
        public string? Body { get; private set; }

        public string? Path { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri!.AbsolutePath;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        }
    }

    private static TeiPassageReranker Client(Tei tei) =>
        new(new HttpClient(tei) { BaseAddress = new Uri("http://reranker/") }, TeiPassageReranker.DefaultModel);

    [Test]
    public async Task Rerank_sendsQueryAndTexts_andReturnsTheScoresInTheOrderOfTheTexts()
    {
        // TEI answers best first.
        var tei = new Tei(HttpStatusCode.OK, """[{"index":2,"score":0.9},{"index":0,"score":0.5},{"index":1,"score":0.1}]""");

        var scores = await Client(tei).RerankAsync("query", ["a", "b", "c"], CancellationToken.None);

        await Assert.That(scores.SequenceEqual([0.5, 0.1, 0.9])).IsTrue();
        await Assert.That(tei.Path).IsEqualTo("/rerank");
        using var body = JsonDocument.Parse(tei.Body!);
        await Assert.That(body.RootElement.GetProperty("query").GetString()).IsEqualTo("query");
        await Assert.That(body.RootElement.GetProperty("texts").GetArrayLength()).IsEqualTo(3);
        await Assert.That(body.RootElement.GetProperty("raw_scores").GetBoolean()).IsFalse();
        await Assert.That(body.RootElement.GetProperty("truncate").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task Rerank_missingScore_returnsFewerScoresThanTexts()
    {
        var tei = new Tei(HttpStatusCode.OK, """[{"index":1,"score":0.4}]""");

        var scores = await Client(tei).RerankAsync("query", ["a", "b"], CancellationToken.None);

        await Assert.That(scores.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments(HttpStatusCode.OK, """[{"index":5,"score":0.4}]""")]
    [Arguments(HttpStatusCode.ServiceUnavailable, """{"error":"loading"}""")]
    public async Task Rerank_badAnswer_throws(HttpStatusCode status, string reply)
    {
        var tei = new Tei(status, reply);

        await Assert.That(async () => await Client(tei).RerankAsync("query", ["a"], CancellationToken.None)).Throws<Exception>();
    }

    [Test]
    public async Task ModelId_isTheConfiguredModel()
    {
        await Assert.That(Client(new Tei(HttpStatusCode.OK, "[]")).ModelId).IsEqualTo("BAAI/bge-reranker-v2-m3");
    }
}
