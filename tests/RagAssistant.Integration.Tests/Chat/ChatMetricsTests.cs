using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using RagAssistant.Integration.Tests.Documents;
using RagAssistant.Integration.Tests.Fixtures;

namespace RagAssistant.Integration.Tests.Chat;

// Spec 017, AC-14 and AC-15: the Chat meter is recorded once per attempt, from the same values as the trace.
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerTestSession)]
[ParallelLimiter<IngestionParallelLimit>]
public sealed class ChatMetricsTests(PostgresFixture postgres)
{
    private sealed record Measurement(string Name, double Value, Dictionary<string, string?> Tags);

    private static MeterListener Listen(IMeterFactory meterFactory, ConcurrentBag<Measurement> sink)
    {
        // Other tests run hosts in this process; only this host's meters (scoped to its factory) are counted.
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == "RagAssistant.Chat" && ReferenceEquals(instrument.Meter.Scope, meterFactory))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => sink.Add(new Measurement(instrument.Name, value, ToDictionary(tags))));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => sink.Add(new Measurement(instrument.Name, value, ToDictionary(tags))));
        listener.Start();
        return listener;
    }

    private static Dictionary<string, string?> ToDictionary(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var result = new Dictionary<string, string?>();
        foreach (var tag in tags)
        {
            result[tag.Key] = tag.Value?.ToString();
        }

        return result;
    }

    [Test]
    public async Task Metrics_refusedCompleteAndFailedRequests_recordOneMeasurementEachWithLowCardinalityLabels()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        var seen = new ConcurrentBag<Measurement>();
        using var listener = Listen(bed.App.Services.GetRequiredService<IMeterFactory>(), seen);
        await bed.AddAsync("Policy", RetrievalBed.P(0.9));
        var (client, id) = await bed.NewConversationAsync();

        var complete = await ChatBed.AskAsync(client, id);
        var refused = await ChatBed.AskAsync(client, id, ChatBed.OffCorpusQuestion);
        bed.Generator.AlwaysFail = true;
        var failed = await ChatBed.AskAsync(client, id, "Another budget question?");

        await Assert.That(complete.Types[^1]).IsEqualTo("answer.completed");
        await Assert.That(refused.Types[^1]).IsEqualTo("answer.completed");
        await Assert.That(failed.Types[^1]).IsEqualTo("error");

        var requests = seen.Where(m => m.Name == "ragassistant.chat.requests").ToList();
        await Assert.That(requests.Count).IsEqualTo(3);
        await Assert.That(requests.Count(m => m.Tags["status"] == "complete" && m.Tags["evidence_outcome"] == "Sufficient")).IsEqualTo(1);
        await Assert.That(requests.Count(m => m.Tags["status"] == "refused" && m.Tags["evidence_outcome"] == "NoEvidence")).IsEqualTo(1);
        await Assert.That(requests.Count(m => m.Tags["status"] == "failed" && m.Tags["evidence_outcome"] == "none")).IsEqualTo(1);

        var refusals = seen.Where(m => m.Name == "ragassistant.chat.refusals").ToList();
        await Assert.That(refusals.Count).IsEqualTo(1);
        await Assert.That(refusals[0].Tags["reason"]).IsEqualTo("no_evidence");

        var errors = seen.Where(m => m.Name == "ragassistant.chat.errors").ToList();
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0].Tags["stage"]).IsEqualTo("retrieval");
        await Assert.That(errors[0].Tags["error_code"]).IsEqualTo("retrieval.embedding_unavailable");

        // Only retrieval that ran records a result count: the complete (1 passage) and the refused (0), not the failed one.
        var results = seen.Where(m => m.Name == "ragassistant.chat.retrieval.results").Select(m => m.Value).Order().ToList();
        await Assert.That(results.SequenceEqual([0d, 1d])).IsTrue();

        // No label may carry an id or text; question.language is en, fr or other (spec 018).
        var allowed = new HashSet<string> { "status", "evidence_outcome", "reason", "stage", "error_code", "question.language" };
        await Assert.That(seen.SelectMany(m => m.Tags.Keys).All(allowed.Contains)).IsTrue();
    }

    [Test]
    public async Task Metrics_firstTokenDuration_isRecordedForAStreamedAnswerAndNotForARefusal()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        var seen = new ConcurrentBag<Measurement>();
        using var listener = Listen(bed.App.Services.GetRequiredService<IMeterFactory>(), seen);
        await bed.AddAsync("Policy", RetrievalBed.P(0.9));
        var (client, id) = await bed.NewConversationAsync();

        await ChatBed.AskAsync(client, id, ChatBed.OffCorpusQuestion);
        await Assert.That(seen.Count(m => m.Name == "ragassistant.chat.first_token.duration")).IsEqualTo(0);

        await ChatBed.AskAsync(client, id);
        await Assert.That(seen.Count(m => m.Name == "ragassistant.chat.first_token.duration")).IsEqualTo(1);
        await Assert.That(seen.Count(m => m.Name == "ragassistant.chat.request.duration")).IsEqualTo(2);
    }
}
