using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RagAssistant.Integration.Tests.Fixtures;
using static RagAssistant.Integration.Tests.Documents.RetrievalBed;

namespace RagAssistant.Integration.Tests.Chat;

// Spec 014, AC-8 to AC-11: ratings and issue reports on finished answers, owner only.
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerTestSession)]
[ParallelLimiter<IngestionParallelLimit>]
public sealed class FeedbackTests(PostgresFixture postgres)
{
    private static string Base(Guid conversation, Guid message) => $"/api/chat/conversations/{conversation}/messages/{message}";

    private static Task<HttpResponseMessage> PutRatingAsync(HttpClient client, Guid conversation, Guid message, string rating) =>
        client.PutAsJsonAsync($"{Base(conversation, message)}/rating", new { rating });

    private static Task<HttpResponseMessage> ReportAsync(HttpClient client, Guid conversation, Guid message, string category = "incorrect", string? comment = null) =>
        client.PostAsJsonAsync($"{Base(conversation, message)}/reports", new { category, comment });

    private static async Task<JsonElement> AssistantAsync(HttpClient client, Guid conversation, Guid message)
    {
        var (_, body) = await client.GetJsonAsync($"/api/chat/conversations/{conversation}");
        return body.GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("id").GetGuid() == message);
    }

    private static async Task<(ChatBed Bed, HttpClient Client, Guid Conversation, Guid Answer)> CompleteAnswerAsync(PostgresFixture postgres)
    {
        var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Policy", P(0.9));
        var (client, id) = await bed.NewConversationAsync();
        var result = await ChatBed.AskAsync(client, id);
        return (bed, client, id, result.AssistantMessageId);
    }

    // AC-8
    [Test]
    public async Task Rating_putReplacesAndDeleteClears_andTheConversationShowsIt()
    {
        var (bed, client, id, answer) = await CompleteAnswerAsync(postgres);
        await using var _ = bed;

        using var helpful = await PutRatingAsync(client, id, answer, "helpful");
        var body = await helpful.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(helpful.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body.GetProperty("rating").GetString()).IsEqualTo("helpful");
        await Assert.That((await AssistantAsync(client, id, answer)).GetProperty("rating").GetString()).IsEqualTo("helpful");

        using var again = await PutRatingAsync(client, id, answer, "helpful");
        await Assert.That(again.StatusCode).IsEqualTo(HttpStatusCode.OK);

        using var replaced = await PutRatingAsync(client, id, answer, "not_helpful");
        await Assert.That(replaced.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await AssistantAsync(client, id, answer)).GetProperty("rating").GetString()).IsEqualTo("not_helpful");
        await Assert.That(await bed.App.ScalarAsync<long>("SELECT count(*) FROM chat.message_ratings WHERE message_id = $1", answer)).IsEqualTo(1);

        using var cleared = await client.DeleteAsync($"{Base(id, answer)}/rating");
        using var clearedAgain = await client.DeleteAsync($"{Base(id, answer)}/rating");
        await Assert.That(cleared.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(clearedAgain.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That((await AssistantAsync(client, id, answer)).GetProperty("rating").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    // AC-9
    [Test]
    public async Task Feedback_onARefusedAnswer_isAllowed()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Policy", P(0.9));
        var (client, id) = await bed.NewConversationAsync();
        var refused = await ChatBed.AskAsync(client, id, ChatBed.OffCorpusQuestion);

        using var rating = await PutRatingAsync(client, id, refused.AssistantMessageId, "not_helpful");
        using var report = await ReportAsync(client, id, refused.AssistantMessageId, "wrong_source", "  It should have found something.  ");
        var created = await report.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(rating.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(report.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(created.GetProperty("comment").GetString()).IsEqualTo("It should have found something.");
        await Assert.That((await AssistantAsync(client, id, refused.AssistantMessageId)).GetProperty("reportCount").GetInt32()).IsEqualTo(1);
    }

    // AC-9
    [Test]
    [Arguments("user")]
    [Arguments("failed")]
    [Arguments("cancelled")]
    [Arguments("pending")]
    public async Task Feedback_onAMessageThatIsNotAFinishedAnswer_returns409(string kind)
    {
        var (bed, client, id, answer) = await CompleteAnswerAsync(postgres);
        await using var _ = bed;
        var target = answer;
        if (kind == "user")
        {
            var (_, body) = await client.GetJsonAsync($"/api/chat/conversations/{id}");
            target = body.GetProperty("messages").EnumerateArray().First(m => m.GetProperty("role").GetString() == "user").GetProperty("id").GetGuid();
        }
        else
        {
            await bed.App.ExecuteAsync("UPDATE chat.messages SET status = $2 WHERE id = $1", answer, char.ToUpperInvariant(kind[0]) + kind[1..]);
        }

        using var rating = await PutRatingAsync(client, id, target, "helpful");
        using var cleared = await client.DeleteAsync($"{Base(id, target)}/rating");
        using var report = await ReportAsync(client, id, target);

        foreach (var response in new[] { rating, cleared, report })
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
            await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()).IsEqualTo("chat.feedback_not_allowed");
        }
    }

    // AC-10
    [Test]
    public async Task Report_eleventh_returns409ReportLimit()
    {
        var (bed, client, id, answer) = await CompleteAnswerAsync(postgres);
        await using var _ = bed;
        for (var i = 0; i < 10; i++)
        {
            using var ok = await ReportAsync(client, id, answer, "outdated");
            await Assert.That(ok.StatusCode).IsEqualTo(HttpStatusCode.Created);
        }

        using var eleventh = await ReportAsync(client, id, answer);

        await Assert.That(eleventh.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That((await eleventh.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()).IsEqualTo("chat.report_limit");
        await Assert.That((await AssistantAsync(client, id, answer)).GetProperty("reportCount").GetInt32()).IsEqualTo(10);
    }

    // AC-10
    [Test]
    public async Task Feedback_invalidInput_returns400()
    {
        var (bed, client, id, answer) = await CompleteAnswerAsync(postgres);
        await using var _ = bed;

        using var longComment = await ReportAsync(client, id, answer, "incorrect", new string('x', 1001));
        using var unknownCategory = await ReportAsync(client, id, answer, "spam");
        using var badRating = await PutRatingAsync(client, id, answer, "meh");

        foreach (var response in new[] { longComment, unknownCategory, badRating })
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()).IsEqualTo("validation.failed");
        }
    }

    // AC-11
    [Test]
    public async Task Feedback_anotherUsersConversation_returns404AndChangesNothing()
    {
        var (bed, _, id, answer) = await CompleteAnswerAsync(postgres);
        await using var _ = bed;
        using var other = bed.ClientFor(ChatRequests.NewUser());

        using var rating = await PutRatingAsync(other, id, answer, "helpful");
        using var cleared = await other.DeleteAsync($"{Base(id, answer)}/rating");
        using var report = await ReportAsync(other, id, answer);

        foreach (var response in new[] { rating, cleared, report })
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }

        await Assert.That(await bed.App.ScalarAsync<long>("SELECT count(*) FROM chat.message_ratings")).IsEqualTo(0);
        await Assert.That(await bed.App.ScalarAsync<long>("SELECT count(*) FROM chat.issue_reports")).IsEqualTo(0);
    }

    [Test]
    public async Task Feedback_unknownMessage_returns404MessageNotFound()
    {
        var (bed, client, id, _) = await CompleteAnswerAsync(postgres);
        await using var _ = bed;

        using var response = await PutRatingAsync(client, id, Guid.NewGuid(), "helpful");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()).IsEqualTo("chat.message_not_found");
    }

    [Test]
    public async Task Retry_clearsTheRatingAndReportsOfTheAnswer()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Policy", P(0.9));
        var (client, id) = await bed.NewConversationAsync();
        var answer = (await ChatBed.AskAsync(client, id)).AssistantMessageId;
        await PutRatingAsync(client, id, answer, "helpful");
        await ReportAsync(client, id, answer);
        await bed.App.ExecuteAsync("UPDATE chat.messages SET status = 'Failed', error_code = 'chat.model_unavailable' WHERE id = $1", answer);

        await ChatBed.RetryAsync(client, id, answer);

        await Assert.That(await bed.App.ScalarAsync<long>("SELECT count(*) FROM chat.message_ratings")).IsEqualTo(0);
        await Assert.That(await bed.App.ScalarAsync<long>("SELECT count(*) FROM chat.issue_reports")).IsEqualTo(0);
    }
}
