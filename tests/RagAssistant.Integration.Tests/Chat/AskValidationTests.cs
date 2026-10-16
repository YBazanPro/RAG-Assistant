using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RagAssistant.Integration.Tests.Documents;
using RagAssistant.Integration.Tests.Fixtures;

namespace RagAssistant.Integration.Tests.Chat;

// Spec 012, AC-7 to AC-10: what is rejected before the stream starts, and the conversation title.
[ClassDataSource<RagAssistantAppFactory>(Shared = SharedType.PerTestSession)]
public sealed class AskValidationTests(RagAssistantAppFactory factory)
{
    private NpgsqlDataSource Db => factory.Services.GetRequiredService<NpgsqlDataSource>();

    public static IEnumerable<string> InvalidBodies()
    {
        yield return """{"question":""}""";
        yield return """{"question":"   "}""";
        yield return $$"""{"question":"{{new string('a', 1001)}}"}""";
        yield return """{"question":"Who approves it?","language":"english"}""";
        yield return $$"""{"question":"Who approves it?","tags":[{{string.Join(',', Enumerable.Range(0, 21).Select(i => $"\"t{i}\""))}}]}""";
        yield return "{}";
    }

    [Test]
    [MethodDataSource(nameof(InvalidBodies))]
    public async Task Ask_invalidRequest_returns400BeforeTheStreamAndPersistsNothing(string body)
    {
        using var client = factory.CreateClientAs(Persona.KnowledgeSeeker, subject: ChatRequests.NewUser());
        var id = await client.CreateConversationAsync();

        var result = await ChatBed.PostStreamAsync(client, $"/api/chat/conversations/{id}/messages", JsonDocument.Parse(body).RootElement);

        await Assert.That(result.Status).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(result.Problem.GetProperty("code").GetString()).IsEqualTo("validation.failed");
        await Assert.That(result.Events.Count).IsEqualTo(0);
        await Assert.That(await DocumentUploads.ScalarAsync<long>(Db, "SELECT count(*) FROM chat.messages WHERE conversation_id = $1", id)).IsEqualTo(0L);
        await Assert.That(await DocumentUploads.ScalarAsync<long>(Db, "SELECT count(*) FROM chat.request_traces WHERE conversation_id = $1", id)).IsEqualTo(0L);
    }

    [Test]
    public async Task Ask_whileAnAnswerIsPending_returns409AnswerInProgress()
    {
        using var client = factory.CreateClientAs(Persona.KnowledgeSeeker, subject: ChatRequests.NewUser());
        var id = await client.CreateConversationAsync();
        await ExecuteAsync(
            """
            INSERT INTO chat.messages (id, conversation_id, role, sequence, text, created_at, filter_tags, status, pending_since, attempts)
            VALUES ($1, $2, 'Assistant', 2, '', now(), '{}', 'Pending', now(), 1)
            """,
            Guid.CreateVersion7(), id);

        var result = await ChatBed.PostStreamAsync(client, $"/api/chat/conversations/{id}/messages", new { question = "Who approves it?" });

        await Assert.That(result.Status).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(result.Problem.GetProperty("code").GetString()).IsEqualTo("chat.answer_in_progress");
    }

    [Test]
    public async Task Ask_conversationAtTheCap_returns409ConversationFull()
    {
        using var client = factory.CreateClientAs(Persona.KnowledgeSeeker, subject: ChatRequests.NewUser());
        var id = await client.CreateConversationAsync();
        await ExecuteAsync("UPDATE chat.conversations SET message_count = 200 WHERE id = $1", id);

        var result = await ChatBed.PostStreamAsync(client, $"/api/chat/conversations/{id}/messages", new { question = "Who approves it?" });

        await Assert.That(result.Status).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(result.Problem.GetProperty("code").GetString()).IsEqualTo("chat.conversation_full");
    }

    [Test]
    public async Task Ask_anotherUsersConversation_returns404()
    {
        using var owner = factory.CreateClientAs(Persona.KnowledgeSeeker, subject: ChatRequests.NewUser());
        var id = await owner.CreateConversationAsync();
        using var other = factory.CreateClientAs(Persona.Maintainer, subject: ChatRequests.NewUser());

        var result = await ChatBed.PostStreamAsync(other, $"/api/chat/conversations/{id}/messages", new { question = "Who approves it?" });

        await Assert.That(result.Status).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(result.Problem.GetProperty("code").GetString()).IsEqualTo("chat.conversation_not_found");
    }

    [Test]
    public async Task Ask_firstQuestionOf120Characters_setsATitleCutAtAWordBoundaryThatTheSecondQuestionKeeps()
    {
        using var client = factory.CreateClientAs(Persona.KnowledgeSeeker, subject: ChatRequests.NewUser());
        var id = await client.CreateConversationAsync();
        var question = string.Join(' ', Enumerable.Repeat("approval", 13)) + " limits?"; // 124 characters

        // The factory's embedding model is unavailable, so the answer fails; the title is set when the question is accepted.
        var first = await ChatBed.AskAsync(client, id, question);
        await ChatBed.AskAsync(client, id, "A completely different second question?");

        await Assert.That(first.Status).IsEqualTo(HttpStatusCode.OK);
        var (_, body) = await client.GetJsonAsync($"/api/chat/conversations/{id}");
        var title = body.GetProperty("title").GetString()!;
        await Assert.That(title.Length).IsLessThanOrEqualTo(80);
        await Assert.That(title).IsEqualTo(string.Join(' ', Enumerable.Repeat("approval", 8)) + "…");
    }

    private async Task ExecuteAsync(string sql, params object[] parameters)
    {
        await using var command = Db.CreateCommand(sql);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }

        await command.ExecuteNonQueryAsync();
    }
}
