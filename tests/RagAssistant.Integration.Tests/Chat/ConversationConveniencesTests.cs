using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RagAssistant.Integration.Tests.Fixtures;
using static RagAssistant.Integration.Tests.Documents.RetrievalBed;

namespace RagAssistant.Integration.Tests.Chat;

// Spec 020, AC-15 to AC-18: rename, search and export of the owner's conversations.
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerTestSession)]
[ParallelLimiter<IngestionParallelLimit>]
public sealed class ConversationConveniencesTests(PostgresFixture postgres)
{
    private static async Task<(HttpStatusCode Status, JsonElement Body)> RenameAsync(HttpClient client, Guid id, string? title)
    {
        using var response = await client.PatchAsJsonAsync($"/api/chat/conversations/{id}", new { title });
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static async Task<string?> ListedTitleAsync(HttpClient client, Guid id)
    {
        var (_, list) = await client.GetJsonAsync("/api/chat/conversations");
        return list.GetProperty("items").EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == id).GetProperty("title").GetString();
    }

    // AC-15
    [Test]
    public async Task Rename_setsTheTitle_whitespaceRevertsToTheFirstQuestion_andTooLongIsRejected()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Policy", P(0.9));
        var (client, id) = await bed.NewConversationAsync();
        await ChatBed.AskAsync(client, id);
        var fifty = new string('t', 50);

        var (renamed, summary) = await RenameAsync(client, id, $"  {fifty}  ");
        var listed = await ListedTitleAsync(client, id);
        var (_, page) = await client.GetJsonAsync($"/api/chat/conversations/{id}");
        // A question after the rename keeps the owner's title.
        await ChatBed.AskAsync(client, id, "Another budget question?");
        var afterQuestion = await ListedTitleAsync(client, id);
        var (reverted, revertedBody) = await RenameAsync(client, id, "   ");
        var (tooLong, problem) = await RenameAsync(client, id, new string('x', 201));

        await Assert.That(renamed).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(summary.GetProperty("title").GetString()).IsEqualTo(fifty);
        await Assert.That(summary.GetProperty("titleEdited").GetBoolean()).IsTrue();
        await Assert.That(listed).IsEqualTo(fifty);
        await Assert.That(page.GetProperty("title").GetString()).IsEqualTo(fifty);
        await Assert.That(afterQuestion).IsEqualTo(fifty);
        await Assert.That(reverted).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(revertedBody.GetProperty("title").GetString()).IsEqualTo(ChatBed.Question);
        await Assert.That(revertedBody.GetProperty("titleEdited").GetBoolean()).IsFalse();
        await Assert.That(tooLong).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(problem.GetProperty("code").GetString()).IsEqualTo("validation.failed");
    }

    // AC-16
    [Test]
    public async Task AnotherUsersConversation_isNotFoundForRenameExportAndGet()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        var (_, id) = await bed.NewConversationAsync();
        using var other = bed.ClientFor(ChatRequests.NewUser());

        var (rename, _) = await RenameAsync(other, id, "Mine now");
        using var export = await other.GetAsync($"/api/chat/conversations/{id}/export");
        var (get, _) = await other.GetJsonAsync($"/api/chat/conversations/{id}");

        await Assert.That(rename).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(export.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(get).IsEqualTo(HttpStatusCode.NotFound);
    }

    // AC-17
    [Test]
    public async Task Search_matchesTitlesAndQuestionsIgnoringCaseAndAccents_onlyForTheOwner()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        var user = ChatRequests.NewUser();
        var (client, leave) = await bed.NewConversationAsync(user);
        var travel = await client.CreateConversationAsync();
        var renamed = await client.CreateConversationAsync();
        await ChatBed.AskAsync(client, leave, "Congés payés : combien de jours ?");
        await ChatBed.AskAsync(client, travel, "Travel budget for 2026?");
        await RenameAsync(client, renamed, "Notes sur les CONGÉS");
        var (otherClient, otherId) = await bed.NewConversationAsync();
        await ChatBed.AskAsync(otherClient, otherId, "Congés payés ?");

        var (status, found) = await client.GetJsonAsync("/api/chat/conversations?q=conges");
        var (_, none) = await client.GetJsonAsync("/api/chat/conversations?q=nothing-like-this");
        var (wildcard, _) = await client.GetJsonAsync("/api/chat/conversations?q=%25");
        var (tooLong, _) = await client.GetJsonAsync($"/api/chat/conversations?q={new string('a', 201)}");

        await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
        Guid[] ids = [.. found.GetProperty("items").EnumerateArray().Select(c => c.GetProperty("id").GetGuid())];
        await Assert.That(ids).IsEquivalentTo([leave, renamed]);
        await Assert.That(found.GetProperty("totalCount").GetInt32()).IsEqualTo(2);
        await Assert.That(none.GetProperty("items").GetArrayLength()).IsEqualTo(0);
        await Assert.That(wildcard).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(tooLong).IsEqualTo(HttpStatusCode.BadRequest);
    }

    // AC-18
    [Test]
    public async Task Export_isAMarkdownAttachmentWithQuestionsAnswersStatusesAndSources()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync(
            new SeedDocument("Budget Policy", "owner"),
            P(0.9, "Department heads approve exceptions.", section: ["Approval", "Thresholds"], page: 4));
        var (client, id) = await bed.NewConversationAsync();
        await ChatBed.AskAsync(client, id);
        await ChatBed.AskAsync(client, id, ChatBed.OffCorpusQuestion);
        // An answer with sufficient evidence but no valid citation is partial (spec 012).
        bed.Chat.Script("Partly answered.");
        await ChatBed.AskAsync(client, id, "Another budget question?");

        using var response = await client.GetAsync($"/api/chat/conversations/{id}/export");
        var markdown = await response.Content.ReadAsStringAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentType!.MediaType).IsEqualTo("text/markdown");
        await Assert.That(response.Content.Headers.ContentDisposition!.DispositionType).IsEqualTo("attachment");
        await Assert.That(response.Content.Headers.ContentDisposition.FileNameStar ?? response.Content.Headers.ContentDisposition.FileName)
            .Matches(@"^""?who-approves-a-budget-exception-\d{4}-\d{2}-\d{2}\.md""?$");
        await Assert.That(markdown).StartsWith($"# {ChatBed.Question}\n");
        await Assert.That(markdown).Contains($"## Question 1\n\n{ChatBed.Question}\n");
        await Assert.That(markdown).Contains($"## Question 2\n\n{ChatBed.OffCorpusQuestion}\n");
        await Assert.That(markdown).Contains("### Answer (refused: the documents do not answer this)");
        await Assert.That(markdown).Contains("### Answer (partial)");
        await Assert.That(markdown).Contains("Department head and Finance [1].");
        await Assert.That(markdown).Contains("- [1] Budget Policy — Approval › Thresholds (version 1, page 4)");
        // No passage text leaves the app with the export.
        await Assert.That(markdown).DoesNotContain("Department heads approve exceptions.");
    }
}
