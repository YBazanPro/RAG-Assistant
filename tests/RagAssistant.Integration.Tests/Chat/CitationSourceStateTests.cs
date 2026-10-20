using System.Text.Json;
using RagAssistant.Integration.Tests.Fixtures;
using RagAssistant.SharedKernel.Common.Text;
using static RagAssistant.Integration.Tests.Documents.RetrievalBed;

namespace RagAssistant.Integration.Tests.Chat;

// Spec 020, AC-10 to AC-14: each citation of a stored answer says how its source stands today.
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerTestSession)]
[ParallelLimiter<IngestionParallelLimit>]
public sealed class CitationSourceStateTests(PostgresFixture postgres)
{
    private const string PassageText = "Department heads approve budget exceptions up to 5,000 euros.";

    private static async Task<(ChatBed Bed, SeededDocument Document, HttpClient Client, Guid ConversationId)> AnsweredAsync(PostgresFixture postgres)
    {
        var bed = await ChatBed.StartAsync(postgres);
        var (doc, _) = await bed.AddAsync(new SeedDocument("Budget Policy", "owner", CreatedBy: "doc-owner"), P(0.9, PassageText));
        var (client, id) = await bed.NewConversationAsync();
        var answer = await ChatBed.AskAsync(client, id);
        await Assert.That(answer.Data("answer.completed").GetProperty("citations").GetArrayLength()).IsEqualTo(1);
        return (bed, doc, client, id);
    }

    private static async Task<string?> StateAsync(HttpClient client, Guid conversationId)
    {
        var (_, body) = await client.GetJsonAsync($"/api/chat/conversations/{conversationId}");
        var answer = body.GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("role").GetString() == "assistant");
        return answer.GetProperty("citations")[0].GetProperty("sourceState").GetString();
    }

    private static async Task NewActiveVersionAsync(ChatBed bed, SeededDocument doc, string text)
    {
        var version = await bed.Seeder.AddVersionAsync(doc.DocumentId, 2, "Indexed", makeActive: true, DateTimeOffset.UtcNow);
        await bed.App.ExecuteAsync("UPDATE documents.document_versions SET status = 'Superseded' WHERE id = $1", doc.VersionId);
        await bed.Seeder.AddEmbeddedPassagesAsync(version, P(0.9, text));
    }

    // AC-14
    [Test]
    public async Task CitationOfTheActiveVersion_isCurrent()
    {
        var (bed, _, client, id) = await AnsweredAsync(postgres);
        await using var _ = bed;

        await Assert.That(await StateAsync(client, id)).IsEqualTo("current");
    }

    // AC-10
    [Test]
    public async Task NewerVersionWithTheSameText_isNewerUnchanged_evenWithOtherWhitespace()
    {
        var (bed, doc, client, id) = await AnsweredAsync(postgres);
        await using var _ = bed;
        await NewActiveVersionAsync(bed, doc, "  Department heads approve budget exceptions\nup to 5,000 euros.  ");

        await Assert.That(await StateAsync(client, id)).IsEqualTo("newer-unchanged");
    }

    // AC-11
    [Test]
    public async Task NewerVersionWithoutTheText_isNewerChanged()
    {
        var (bed, doc, client, id) = await AnsweredAsync(postgres);
        await using var _ = bed;
        await NewActiveVersionAsync(bed, doc, "Department heads approve budget exceptions up to 8,000 euros.");

        await Assert.That(await StateAsync(client, id)).IsEqualTo("newer-changed");
    }

    // AC-12
    [Test]
    public async Task ArchivedDocument_isArchived()
    {
        var (bed, doc, client, id) = await AnsweredAsync(postgres);
        await using var _ = bed;
        await bed.App.ExecuteAsync("UPDATE documents.documents SET status = 'Archived' WHERE id = $1", doc.DocumentId);

        await Assert.That(await StateAsync(client, id)).IsEqualTo("archived");
    }

    // AC-13
    [Test]
    public async Task DocumentMadePrivateByItsOwner_isUnavailable()
    {
        var (bed, doc, client, id) = await AnsweredAsync(postgres);
        await using var _ = bed;
        await bed.App.ExecuteAsync("UPDATE documents.documents SET visibility = 'Private' WHERE id = $1", doc.DocumentId);

        await Assert.That(await StateAsync(client, id)).IsEqualTo("unavailable");
    }

    [Test]
    public async Task Snapshot_storesTheHashOfItsText()
    {
        var (bed, _, _, _) = await AnsweredAsync(postgres);
        await using var _ = bed;

        var hash = await bed.App.ScalarAsync<string>("SELECT text_hash FROM chat.citation_snapshots LIMIT 1");

        await Assert.That(hash).IsEqualTo(PassageTextHash.Compute(PassageText));
    }

    // The migrations backfill old rows in SQL: that expression must give the hash the code computes.
    [Test]
    [Arguments("plain text")]
    [Arguments("  tabs\tand\nnew lines \r\n collapse  ")]
    [Arguments("é composed to é, non breaking space kept")]
    public async Task SqlBackfillExpression_matchesTheCode(string text)
    {
        await using var bed = await ChatBed.StartAsync(postgres);

        var sql = await bed.App.ScalarAsync<string>($"SELECT {PassageTextHash.SqlExpression("$1::text")}", text);

        await Assert.That(sql).IsEqualTo(PassageTextHash.Compute(text));
    }
}
