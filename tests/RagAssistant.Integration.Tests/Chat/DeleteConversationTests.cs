using System.Net;
using System.Net.Http.Json;
using RagAssistant.Integration.Tests.Fixtures;
using static RagAssistant.Integration.Tests.Documents.RetrievalBed;

namespace RagAssistant.Integration.Tests.Chat;

// Spec 014, AC-11 to AC-13: deleting a conversation removes everything of it, and only the owner can.
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerTestSession)]
[ParallelLimiter<IngestionParallelLimit>]
public sealed class DeleteConversationTests(PostgresFixture postgres)
{
    // The other conversation has no rating or report, so only these tables must still hold its rows.
    private static readonly string[] Tables = ["conversations", "messages", "citation_snapshots", "request_traces"];

    private static async Task<long> RowsOfAsync(ChatBed bed, Guid conversation) => await bed.App.ScalarAsync<long>(
        """
        SELECT (SELECT count(*) FROM chat.conversations WHERE id = $1)
             + (SELECT count(*) FROM chat.messages WHERE conversation_id = $1)
             + (SELECT count(*) FROM chat.citation_snapshots WHERE message_id IN (SELECT id FROM chat.messages WHERE conversation_id = $1))
             + (SELECT count(*) FROM chat.message_ratings WHERE message_id IN (SELECT id FROM chat.messages WHERE conversation_id = $1))
             + (SELECT count(*) FROM chat.issue_reports WHERE message_id IN (SELECT id FROM chat.messages WHERE conversation_id = $1))
             + (SELECT count(*) FROM chat.request_traces WHERE conversation_id = $1)
        """,
        conversation);

    // AC-12
    [Test]
    public async Task Delete_removesTheConversationAndEverythingOfItAndNothingElse()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Policy", P(0.9));
        var user = ChatRequests.NewUser();
        var (client, doomed) = await bed.NewConversationAsync(user);
        var kept = await client.CreateConversationAsync();
        var answer = (await ChatBed.AskAsync(client, doomed)).AssistantMessageId;
        await ChatBed.AskAsync(client, kept);
        await client.PutAsJsonAsync($"/api/chat/conversations/{doomed}/messages/{answer}/rating", new { rating = "helpful" });
        await client.PostAsJsonAsync($"/api/chat/conversations/{doomed}/messages/{answer}/reports", new { category = "incorrect" });
        await Assert.That(await RowsOfAsync(bed, doomed)).IsGreaterThan(6);
        var keptBefore = await RowsOfAsync(bed, kept);

        using var response = await client.DeleteAsync($"/api/chat/conversations/{doomed}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(await RowsOfAsync(bed, doomed)).IsEqualTo(0);
        await Assert.That(await RowsOfAsync(bed, kept)).IsEqualTo(keptBefore);
        foreach (var table in Tables)
        {
            await Assert.That(await bed.App.ScalarAsync<long>($"SELECT count(*) FROM chat.{table}")).IsGreaterThan(0);
        }

        using var listed = await client.GetAsync($"/api/chat/conversations/{doomed}");
        await Assert.That(listed.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    // AC-11
    [Test]
    public async Task Delete_anotherUsersConversation_returns404AndChangesNothing()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Policy", P(0.9));
        var (client, id) = await bed.NewConversationAsync();
        await ChatBed.AskAsync(client, id);
        var before = await RowsOfAsync(bed, id);
        using var other = bed.ClientFor(ChatRequests.NewUser());

        using var response = await other.DeleteAsync($"/api/chat/conversations/{id}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await RowsOfAsync(bed, id)).IsEqualTo(before);
    }

    // AC-13
    [Test]
    public async Task Delete_whileAnAnswerIsLive_returns409_butAStalePendingAnswerDoesNotBlock()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Policy", P(0.9));
        var (client, id) = await bed.NewConversationAsync();
        var answer = (await ChatBed.AskAsync(client, id)).AssistantMessageId;
        await bed.App.ExecuteAsync("UPDATE chat.messages SET status = 'Pending', pending_since = now() WHERE id = $1", answer);

        using var live = await client.DeleteAsync($"/api/chat/conversations/{id}");
        await bed.App.ExecuteAsync("UPDATE chat.messages SET pending_since = now() - interval '1 day' WHERE id = $1", answer);
        using var stale = await client.DeleteAsync($"/api/chat/conversations/{id}");

        await Assert.That(live.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That((await live.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("code").GetString()).IsEqualTo("chat.answer_in_progress");
        await Assert.That(stale.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(await RowsOfAsync(bed, id)).IsEqualTo(0);
    }

    [Test]
    public async Task Delete_twiceAtOnce_givesOneNoContentAndOneNotFound()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        var (client, id) = await bed.NewConversationAsync();

        var results = await Task.WhenAll(client.DeleteAsync($"/api/chat/conversations/{id}"), client.DeleteAsync($"/api/chat/conversations/{id}"));

        await Assert.That(results.Select(r => r.StatusCode).Order()).IsEquivalentTo([HttpStatusCode.NoContent, HttpStatusCode.NotFound]);
    }
}
