using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RagAssistant.Integration.Tests.Fixtures;
using static RagAssistant.Integration.Tests.Documents.RetrievalBed;

namespace RagAssistant.Integration.Tests.Chat;

// Spec 016, AC-20 to AC-23: private documents in conversations (scope, activity, answers already given).
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerTestSession)]
[ParallelLimiter<IngestionParallelLimit>]
public sealed class ScopeVisibilityTests(PostgresFixture postgres)
{
    private const string DocumentOwner = "doc-owner";

    private static SeedDocument Shared(string title) => new(title, "owner", CreatedBy: DocumentOwner);

    private static Task MakePrivateAsync(ChatBed bed, Guid documentId) =>
        bed.App.ExecuteAsync("UPDATE documents.documents SET visibility = 'Private' WHERE id = $1", documentId);

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PutScopeAsync(HttpClient client, Guid conversationId, params Guid[] documentIds)
    {
        using var response = await client.PutAsJsonAsync($"/api/chat/conversations/{conversationId}/scope", new { documentIds, tags = Array.Empty<string>() });
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static string ActivityUrl(Guid conversationId, Guid messageId) => $"/api/chat/conversations/{conversationId}/messages/{messageId}/activity";

    // AC-20
    [Test]
    public async Task Ask_afterTheScopeDocumentTurnedPrivate_excludesItHidesItsTitleAndRefuses()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        var (doc, _) = await bed.AddAsync(Shared("Budget Policy"), P(0.95));
        var (client, id) = await bed.NewConversationAsync();
        await PutScopeAsync(client, id, doc.DocumentId);
        await MakePrivateAsync(bed, doc.DocumentId);

        var result = await ChatBed.AskAsync(client, id);
        var conversation = (await client.GetJsonAsync($"/api/chat/conversations/{id}")).Body.GetProperty("scope");
        var activity = (await client.GetJsonAsync(ActivityUrl(id, result.AssistantMessageId))).Body;

        await Assert.That(result.Data("answer.completed").GetProperty("status").GetString()).IsEqualTo("refused");
        await Assert.That(bed.Chat.Calls.Count).IsEqualTo(0);
        var chip = conversation.GetProperty("documents").EnumerateArray().Single();
        await Assert.That(chip.GetProperty("available").GetBoolean()).IsFalse();
        await Assert.That(chip.GetProperty("title").ValueKind).IsEqualTo(JsonValueKind.Null);
        var left = activity.GetProperty("scope").GetProperty("documents").EnumerateArray().Single();
        await Assert.That(left.GetProperty("available").GetBoolean()).IsFalse();
        await Assert.That(left.GetProperty("title").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    // AC-20: the owner of the document still gets everything
    [Test]
    public async Task Ask_asTheOwnerOfAPrivateDocument_stillFindsIt()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        var (doc, _) = await bed.AddAsync(new SeedDocument("Budget Policy", "owner", Visibility: "Private", CreatedBy: DocumentOwner), P(0.95));
        bed.Chat.Script("Mine [1].");
        var (client, id) = await bed.NewConversationAsync(DocumentOwner);

        var result = await ChatBed.AskAsync(client, id);

        await Assert.That(result.Data("retrieval.completed").GetProperty("sources")[0].GetProperty("documentId").GetGuid()).IsEqualTo(doc.DocumentId);
    }

    // AC-21, AC-22
    [Test]
    public async Task AnswerAlreadyGiven_keepsItsSnapshots_marksTheDocumentUnreadable_andBlanksOnlyUncitedTitles()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        var (cited, _) = await bed.AddAsync(Shared("Cited Policy"), P(0.95));
        var (uncited, _) = await bed.AddAsync(Shared("Uncited Policy"), P(0.85));
        bed.Chat.Script("Per policy [1].");
        var (client, id) = await bed.NewConversationAsync();
        var first = await ChatBed.AskAsync(client, id);
        await MakePrivateAsync(bed, cited.DocumentId);
        await MakePrivateAsync(bed, uncited.DocumentId);

        var conversation = (await client.GetJsonAsync($"/api/chat/conversations/{id}")).Body;
        var activity = (await client.GetJsonAsync(ActivityUrl(id, first.AssistantMessageId))).Body;

        var citation = conversation.GetProperty("messages").EnumerateArray().Last().GetProperty("citations").EnumerateArray().Single();
        await Assert.That(citation.GetProperty("documentTitle").GetString()).IsEqualTo("Cited Policy");
        await Assert.That(citation.GetProperty("text").GetString()).IsNotEmpty();
        await Assert.That(citation.GetProperty("documentReadable").GetBoolean()).IsFalse();
        var passages = activity.GetProperty("retrieval").GetProperty("passages").EnumerateArray().ToArray();
        var citedStep = passages.Single(p => p.GetProperty("cited").GetBoolean());
        var uncitedStep = passages.Single(p => !p.GetProperty("cited").GetBoolean());
        await Assert.That(citedStep.GetProperty("documentTitle").GetString()).IsEqualTo("Cited Policy");
        await Assert.That(uncitedStep.GetProperty("documentTitle").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    // AC-21
    [Test]
    public async Task Retry_afterTheDocumentTurnedPrivate_noLongerRetrievesIt()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        var (doc, _) = await bed.AddAsync(Shared("Budget Policy"), P(0.95));
        bed.Chat.ThrowAfter(0);
        var (client, id) = await bed.NewConversationAsync();
        var first = await ChatBed.AskAsync(client, id);
        await MakePrivateAsync(bed, doc.DocumentId);

        var retried = await ChatBed.RetryAsync(client, id, first.AssistantMessageId);

        await Assert.That(retried.Data("answer.completed").GetProperty("status").GetString()).IsEqualTo("refused");
    }

    // AC-23
    [Test]
    public async Task PutScope_namingAPrivateDocumentOfSomeoneElse_looksLikeAnUnknownId()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        var (secret, _) = await bed.AddAsync(new SeedDocument("Secret", "owner", Visibility: "Private", CreatedBy: DocumentOwner), P(0.9));
        var unknown = Guid.NewGuid();
        var (stranger, strangerConversation) = await bed.NewConversationAsync();
        var (owner, ownerConversation) = await bed.NewConversationAsync(DocumentOwner);

        var denied = await PutScopeAsync(stranger, strangerConversation, secret.DocumentId);
        var missing = await PutScopeAsync(stranger, strangerConversation, unknown);
        var allowed = await PutScopeAsync(owner, ownerConversation, secret.DocumentId);

        await Assert.That(denied.Status).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(missing.Status).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(denied.Body.GetProperty("code").GetString()).IsEqualTo(missing.Body.GetProperty("code").GetString());
        await Assert.That(allowed.Status).IsEqualTo(HttpStatusCode.OK);
    }
}
