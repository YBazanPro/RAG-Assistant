using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RagAssistant.Integration.Tests.Fixtures;
using static RagAssistant.Integration.Tests.Documents.RetrievalBed;

namespace RagAssistant.Integration.Tests.Chat;

// Spec 015, AC-1 to AC-10: a conversation's scope narrows retrieval to chosen documents and tags.
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerTestSession)]
[ParallelLimiter<IngestionParallelLimit>]
public sealed class ScopeTests(PostgresFixture postgres)
{
    private static readonly string[] LegalOnly = ["legal"];

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PutScopeAsync(
        HttpClient client, Guid conversationId, Guid[] documentIds, string[]? tags = null)
    {
        using var response = await client.PutAsJsonAsync($"/api/chat/conversations/{conversationId}/scope", new { documentIds, tags = tags ?? [] });
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static Guid[] SourceDocuments(StreamResult result) =>
        [.. result.Data("retrieval.completed").GetProperty("sources").EnumerateArray().Select(s => s.GetProperty("documentId").GetGuid())];

    // AC-1
    [Test]
    public async Task Ask_withoutScope_searchesTheWholeCorpus()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("A", P(0.9));
        await bed.AddAsync("B", P(0.8));
        bed.Chat.Script("Both [1] [2].");
        var (client, id) = await bed.NewConversationAsync();

        var result = await ChatBed.AskAsync(client, id);

        await Assert.That(SourceDocuments(result).Length).IsEqualTo(2);
    }

    // AC-2, AC-5
    [Test]
    public async Task Ask_withDocumentScope_onlyThoseDocumentsAreSearched_followUpsIncluded()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        var (a, _) = await bed.AddAsync("A", P(0.8));
        await bed.AddAsync("B", P(0.95));
        bed.Chat.Script("From A [1].");
        var (client, id) = await bed.NewConversationAsync();
        var put = await PutScopeAsync(client, id, [a.DocumentId]);

        var first = await ChatBed.AskAsync(client, id);
        bed.Chat.ScriptRewrite(ChatBed.Question);
        bed.Chat.Script("From A again [1].");
        var second = await ChatBed.AskAsync(client, id, "And above that amount?");

        await Assert.That(put.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(SourceDocuments(first).SequenceEqual([a.DocumentId])).IsTrue();
        await Assert.That(SourceDocuments(second).SequenceEqual([a.DocumentId])).IsTrue();
    }

    // AC-3
    [Test]
    public async Task Ask_withDocumentsAndTags_tagsNarrowWithinTheChosenDocuments()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        var (tagged, _) = await bed.AddAsync(new SeedDocument("Tagged", "owner", Tags: ["hr"]), P(0.8));
        var (plain, _) = await bed.AddAsync(new SeedDocument("Plain", "owner"), P(0.9));
        await bed.AddAsync(new SeedDocument("Outside", "owner", Tags: ["hr"]), P(0.95));
        bed.Chat.Script("Tagged [1].");
        var (client, id) = await bed.NewConversationAsync();
        await PutScopeAsync(client, id, [tagged.DocumentId, plain.DocumentId], ["HR "]);

        var result = await ChatBed.AskAsync(client, id);

        await Assert.That(SourceDocuments(result).SequenceEqual([tagged.DocumentId])).IsTrue();
    }

    // AC-4
    [Test]
    public async Task Ask_withTagScopeOnly_filtersTheWholeCorpusByThoseTags()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        var (hr, _) = await bed.AddAsync(new SeedDocument("Hr", "owner", Tags: ["hr"]), P(0.8));
        await bed.AddAsync("Other", P(0.95));
        bed.Chat.Script("Hr [1].");
        var (client, id) = await bed.NewConversationAsync();
        await PutScopeAsync(client, id, [], ["hr"]);

        var result = await ChatBed.AskAsync(client, id);

        await Assert.That(SourceDocuments(result).SequenceEqual([hr.DocumentId])).IsTrue();
    }

    [Test]
    public async Task Ask_requestTags_replaceTheScopeTagsForThatQuestion()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        var (hr, _) = await bed.AddAsync(new SeedDocument("Hr", "owner", Tags: ["hr"]), P(0.8));
        var (legal, _) = await bed.AddAsync(new SeedDocument("Legal", "owner", Tags: ["legal"]), P(0.9));
        bed.Chat.Script("Legal [1].");
        var (client, id) = await bed.NewConversationAsync();
        await PutScopeAsync(client, id, [hr.DocumentId, legal.DocumentId], ["hr"]);

        var result = await ChatBed.PostStreamAsync(
            client, $"/api/chat/conversations/{id}/messages", new { question = ChatBed.Question, tags = LegalOnly });

        await Assert.That(SourceDocuments(result).SequenceEqual([legal.DocumentId])).IsTrue();
    }

    // AC-6
    [Test]
    public async Task Ask_whenAChosenDocumentIsArchived_itIsLeftOutAndReportedUnavailable()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        var (live, _) = await bed.AddAsync("Live", P(0.8));
        var (old, _) = await bed.AddAsync(new SeedDocument("Old", "owner", Archived: true), P(0.95));
        bed.Chat.Script("Live [1].");
        var (client, id) = await bed.NewConversationAsync();
        await PutScopeAsync(client, id, [live.DocumentId, old.DocumentId]);

        var result = await ChatBed.AskAsync(client, id);
        var conversation = await client.GetJsonAsync($"/api/chat/conversations/{id}");

        await Assert.That(SourceDocuments(result).SequenceEqual([live.DocumentId])).IsTrue();
        await Assert.That(conversation.Body.GetProperty("scope").GetProperty("unavailableDocumentIds").EnumerateArray().Select(e => e.GetGuid()).SequenceEqual([old.DocumentId])).IsTrue();
        var traces = await bed.App.RowsAsync("SELECT filter_document_ids, unavailable_document_ids FROM chat.request_traces WHERE assistant_message_id = $1", result.AssistantMessageId);
        await Assert.That(((Guid[])traces.Single()[0]!).Length).IsEqualTo(2);
        await Assert.That(((Guid[])traces.Single()[1]!).SequenceEqual([old.DocumentId])).IsTrue();
    }

    // AC-7
    [Test]
    public async Task Ask_whenEveryChosenDocumentIsUnavailable_refusesWithoutAModelCall()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Live", P(0.95));
        var (old, _) = await bed.AddAsync(new SeedDocument("Old", "owner", Archived: true), P(0.95));
        var (client, id) = await bed.NewConversationAsync();
        await PutScopeAsync(client, id, [old.DocumentId]);

        var result = await ChatBed.AskAsync(client, id);

        await Assert.That(result.Data("answer.completed").GetProperty("status").GetString()).IsEqualTo("refused");
        await Assert.That(bed.Chat.Calls.Count).IsEqualTo(0);
    }

    // AC-8 (data): the scope an answer used is stored with it, later changes do not touch it
    [Test]
    public async Task Ask_storesTheScopeOnTheQuestionAndTheTrace_andLaterChangesDoNotRewriteIt()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        var (a, _) = await bed.AddAsync("A", P(0.8));
        var (b, _) = await bed.AddAsync("B", P(0.9));
        bed.Chat.Script("A [1].");
        var (client, id) = await bed.NewConversationAsync();
        await PutScopeAsync(client, id, [a.DocumentId]);
        var result = await ChatBed.AskAsync(client, id);

        await PutScopeAsync(client, id, [b.DocumentId]);

        var traces = await bed.App.RowsAsync("SELECT filter_document_ids FROM chat.request_traces WHERE assistant_message_id = $1", result.AssistantMessageId);
        await Assert.That(((Guid[])traces.Single()[0]!).SequenceEqual([a.DocumentId])).IsTrue();
        var messages = await bed.App.RowsAsync("SELECT filter_document_ids FROM chat.messages WHERE id = $1", result.Data("question.accepted").GetProperty("userMessageId").GetGuid());
        await Assert.That(((Guid[])messages.Single()[0]!).SequenceEqual([a.DocumentId])).IsTrue();
    }

    // AC-9
    [Test]
    public async Task PutScope_unknownDocumentOrTooManyItems_returns400AndKeepsTheScope()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        var (a, _) = await bed.AddAsync("A", P(0.8));
        var (client, id) = await bed.NewConversationAsync();
        await PutScopeAsync(client, id, [a.DocumentId], ["hr"]);
        var unknown = Guid.NewGuid();

        var missing = await PutScopeAsync(client, id, [a.DocumentId, unknown]);
        var tooManyDocuments = await PutScopeAsync(client, id, [.. Enumerable.Range(0, 26).Select(_ => Guid.NewGuid())]);
        var tooManyTags = await PutScopeAsync(client, id, [], [.. Enumerable.Range(0, 21).Select(i => $"t{i}")]);

        await Assert.That(missing.Status).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(missing.Body.GetProperty("code").GetString()).IsEqualTo("validation.failed");
        await Assert.That(missing.Body.GetProperty("errors").GetProperty("documentIds")[0].GetString()).Contains(unknown.ToString());
        await Assert.That(tooManyDocuments.Status).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(tooManyTags.Status).IsEqualTo(HttpStatusCode.BadRequest);
        var scope = (await client.GetJsonAsync($"/api/chat/conversations/{id}")).Body.GetProperty("scope");
        await Assert.That(scope.GetProperty("documentIds").EnumerateArray().Select(e => e.GetGuid()).SequenceEqual([a.DocumentId])).IsTrue();
        await Assert.That(scope.GetProperty("tags").EnumerateArray().Select(e => e.GetString()).SequenceEqual(["hr"])).IsTrue();
    }

    // AC-10
    [Test]
    public async Task PutScope_onSomeoneElsesConversation_returns404()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        var (a, _) = await bed.AddAsync("A", P(0.8));
        var (_, id) = await bed.NewConversationAsync();
        var stranger = bed.ClientFor(ChatRequests.NewUser());

        var result = await PutScopeAsync(stranger, id, [a.DocumentId]);

        await Assert.That(result.Status).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task PutScope_isIdempotentAndNormalizes()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        var (a, _) = await bed.AddAsync("A", P(0.8));
        var (client, id) = await bed.NewConversationAsync();

        var first = await PutScopeAsync(client, id, [a.DocumentId, a.DocumentId], [" HR", "hr", ""]);
        var second = await PutScopeAsync(client, id, [a.DocumentId], ["hr"]);

        await Assert.That(first.Body.ToString()).IsEqualTo(second.Body.ToString());
        await Assert.That(first.Body.GetProperty("documentIds").GetArrayLength()).IsEqualTo(1);
        await Assert.That(first.Body.GetProperty("tags").EnumerateArray().Select(e => e.GetString()).SequenceEqual(["hr"])).IsTrue();
    }
}
