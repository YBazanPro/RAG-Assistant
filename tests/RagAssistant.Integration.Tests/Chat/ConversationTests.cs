using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RagAssistant.Integration.Tests.Documents;
using RagAssistant.Integration.Tests.Fixtures;

namespace RagAssistant.Integration.Tests.Chat;

// Spec 012, AC-1, AC-2, AC-5 and AC-31. Every test signs in as a user of its own: the factory's database is shared.
[ClassDataSource<RagAssistantAppFactory>(Shared = SharedType.PerTestSession)]
public sealed class ConversationTests(RagAssistantAppFactory factory)
{
    private NpgsqlDataSource Db => factory.Services.GetRequiredService<NpgsqlDataSource>();

    [Test]
    public async Task Create_signedInSeeker_returns201WithNullTitleAndStoresTheOwnerSub()
    {
        var user = ChatRequests.NewUser();
        using var client = factory.CreateClientAs(Persona.KnowledgeSeeker, subject: user);

        using var response = await client.PostAsync("/api/chat/conversations", content: null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetGuid();
        await Assert.That(body.GetProperty("title").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(response.Headers.Location?.OriginalString).IsEqualTo($"/api/chat/conversations/{id}");
        await Assert.That(await DocumentUploads.ScalarAsync<string>(Db, "SELECT owner_sub FROM chat.conversations WHERE id = $1", id))
            .IsEqualTo(user);
    }

    [Test]
    public async Task List_twoUsers_returnsOnlyTheCallersConversationsNewestActivityFirst()
    {
        var a = ChatRequests.NewUser();
        using var clientA = factory.CreateClientAs(Persona.KnowledgeSeeker, subject: a);
        using var clientB = factory.CreateClientAs(Persona.KnowledgeSeeker, subject: ChatRequests.NewUser());
        var first = await clientA.CreateConversationAsync();
        var second = await clientA.CreateConversationAsync();
        var third = await clientA.CreateConversationAsync();
        await clientB.CreateConversationAsync();
        await ExecuteAsync("UPDATE chat.conversations SET last_activity_at = now() + interval '1 hour' WHERE id = $1", first);

        var (status, body) = await clientA.GetJsonAsync("/api/chat/conversations");

        await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body.GetProperty("totalCount").GetInt32()).IsEqualTo(3);
        await Assert.That(body.GetProperty("pageSize").GetInt32()).IsEqualTo(20);
        var ids = body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToArray();
        await Assert.That(ids.Same(first, third, second)).IsTrue();
    }

    [Test]
    [Arguments("pageSize=101")]
    [Arguments("page=0")]
    [Arguments("page=abc")]
    public async Task List_invalidPaging_returns400(string query)
    {
        using var client = factory.CreateClientAs(Persona.KnowledgeSeeker, subject: ChatRequests.NewUser());

        var (status, body) = await client.GetJsonAsync($"/api/chat/conversations?{query}");

        await Assert.That(status).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(body.GetProperty("code").GetString()).IsEqualTo("validation.failed");
    }

    [Test]
    public async Task Get_newConversation_returnsItWithNoMessages()
    {
        using var client = factory.CreateClientAs(Persona.KnowledgeSeeker, subject: ChatRequests.NewUser());
        var id = await client.CreateConversationAsync();

        var (status, body) = await client.GetJsonAsync($"/api/chat/conversations/{id}");

        await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body.GetProperty("id").GetGuid()).IsEqualTo(id);
        await Assert.That(body.GetProperty("messages").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task Migrations_applied_createTheChatTablesWithTheirOwnHistory()
    {
        var tables = await DocumentUploads.ScalarAsync<string>(
            Db,
            "SELECT string_agg(table_name, ',' ORDER BY table_name) FROM information_schema.tables " +
            "WHERE table_schema = 'chat' AND table_name <> '__EFMigrationsHistory'");

        await Assert.That(tables).IsEqualTo("citation_snapshots,conversations,messages,request_traces");
        await Assert.That(await DocumentUploads.ScalarAsync<long>(Db, "SELECT count(*) FROM chat.\"__EFMigrationsHistory\""))
            .IsGreaterThanOrEqualTo(1L);
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
