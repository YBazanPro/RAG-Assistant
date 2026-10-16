using RagAssistant.Integration.Tests.Fixtures;
using static RagAssistant.Integration.Tests.Documents.RetrievalBed;

namespace RagAssistant.Integration.Tests.Chat;

// Spec 012, AC-28 (ADR 0004): citations render from their snapshot, whatever happens to the source afterwards.
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerTestSession)]
[ParallelLimiter<IngestionParallelLimit>]
public sealed class ChatProvenanceTests(PostgresFixture postgres)
{
    [Test]
    public async Task Get_citedDocumentChangedArchivedAndReplaced_stillReturnsTheOriginalSnapshot()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        var (doc, passages) = await bed.AddAsync("Budget Exception Policy", P(0.9, "Exceptions need the department head."));
        var (client, id) = await bed.NewConversationAsync();
        await ChatBed.AskAsync(client, id);

        await bed.App.ExecuteAsync("UPDATE documents.passages SET text = 'Rewritten text.' WHERE id = $1", passages[0]);
        await bed.App.ExecuteAsync("UPDATE documents.documents SET title = 'Renamed Policy', status = 'Archived' WHERE id = $1", doc.DocumentId);
        await bed.App.ExecuteAsync("UPDATE documents.document_versions SET status = 'Superseded' WHERE id = $1", doc.VersionId);
        await bed.Seeder.AddVersionAsync(doc.DocumentId, 2, "Indexed", makeActive: true, DateTimeOffset.UtcNow, passages: 1);

        var (_, body) = await client.GetJsonAsync($"/api/chat/conversations/{id}");

        var citation = body.GetProperty("messages")[1].GetProperty("citations")[0];
        await Assert.That(citation.GetProperty("text").GetString()).IsEqualTo("Exceptions need the department head.");
        await Assert.That(citation.GetProperty("documentTitle").GetString()).IsEqualTo("Budget Exception Policy");
        await Assert.That(citation.GetProperty("versionNumber").GetInt32()).IsEqualTo(1);
        await Assert.That(citation.GetProperty("versionId").GetGuid()).IsEqualTo(doc.VersionId);
        await Assert.That(citation.GetProperty("passageId").GetGuid()).IsEqualTo(passages[0]);
    }
}
