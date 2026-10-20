using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RagAssistant.Integration.Tests.Chat;
using RagAssistant.Integration.Tests.Fixtures;
using static RagAssistant.Integration.Tests.Documents.LifecycleHelpers;

namespace RagAssistant.Integration.Tests.Documents;

// Spec 020, AC-1 to AC-7: suggested questions generated after indexing and checked by retrieval, the owner's edits, and the
// home page's reads.
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerTestSession)]
[ParallelLimiter<IngestionParallelLimit>]
public sealed class SuggestedQuestionsTests(PostgresFixture postgres)
{
    private static readonly Dictionary<string, string> On = new() { ["Documents:Suggestions:Enabled"] = "true" };

    // Every passage carries a scripted keyword, so the only vectors in the index are exact axes: a question with the same
    // keyword finds its passage with cosine 1, and one with an unused axis finds nothing.
    private const string Handbook = "# Handbook\n\n## Alpha\n\nAlpha travel is booked two weeks ahead.\n\n## Beta\n\nBeta expenses need a receipt.\n";

    private static FakeEmbeddingGenerator Vectors()
    {
        var generator = new FakeEmbeddingGenerator();
        generator.Script("alpha", FakeEmbeddingGenerator.Axis(2));
        generator.Script("beta", FakeEmbeddingGenerator.Axis(3));
        generator.Script("gamma", FakeEmbeddingGenerator.Axis(5));
        return generator;
    }

    private static async Task<JsonElement> DocumentAsync(HttpClient client, Guid documentId) =>
        (await client.GetJsonAsync($"/api/documents/{documentId}")).Body;

    private static async Task WaitForSuggestionsAsync(IngestionHarness app, Guid versionId) =>
        await IngestionHarness.WaitUntilAsync(
            async () => await app.ScalarAsync<string>("SELECT suggestions_status FROM documents.document_versions WHERE id = $1", versionId) is not (null or "Pending"),
            "the suggestion step to finish");

    private static async Task<HttpResponseMessage> PutSuggestionsAsync(HttpClient client, Guid documentId, string? ifMatch, params string[] questions)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/documents/{documentId}/suggestions") { Content = JsonContent.Create(new { questions }) };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await client.SendAsync(request);
    }

    // AC-1
    [Test]
    public async Task Indexing_keepsOnlyTheQuestionsRetrievalFindsConfidently()
    {
        var chat = new ScriptedChatClient();
        chat.ScriptSuggestions("When is alpha travel booked?", "What do beta expenses need?", "What is the gamma rule?");
        await using var app = await IngestionHarness.StartAsync(postgres, generator: Vectors(), settings: On, chat: chat);
        var doc = await ImportIndexedAsync(app, Handbook);
        await WaitForSuggestionsAsync(app, doc.VersionId);
        using var owner = app.CreateClientAs(Persona.ContentOwner);

        var body = await DocumentAsync(owner, doc.DocumentId);

        await Assert.That(body.GetProperty("suggestionsStatus").GetString()).IsEqualTo("ready");
        string[] kept = [.. body.GetProperty("suggestedQuestions").EnumerateArray().Select(q => q.GetString()!)];
        await Assert.That(kept.SequenceEqual(["When is alpha travel booked?", "What do beta expenses need?"])).IsTrue();
        await Assert.That(chat.SuggestionCalls.Count).IsEqualTo(1);
        await Assert.That(chat.RewriteCalls.Count).IsEqualTo(0);
        // The step is recorded after "completed", as its own stage.
        await Assert.That((await app.EventsAsync(doc.RunId))[^1]).IsEqualTo("Suggestions/Completed");
    }

    // AC-2
    [Test]
    public async Task ChatModelFailing_leavesTheVersionIndexedAndMarksTheSuggestionsFailed()
    {
        var chat = new ScriptedChatClient();
        chat.ThrowOnSuggestions();
        await using var app = await IngestionHarness.StartAsync(postgres, generator: Vectors(), settings: On, chat: chat);
        var doc = await ImportIndexedAsync(app, Handbook);
        await WaitForSuggestionsAsync(app, doc.VersionId);
        using var owner = app.CreateClientAs(Persona.ContentOwner);
        using var maintainer = app.CreateClientAs(Persona.Maintainer);

        var body = await DocumentAsync(owner, doc.DocumentId);
        var (_, search) = await maintainer.GetJsonAsync("/api/documents/search?q=alpha%20travel");

        await Assert.That(body.GetProperty("status").GetString()).IsEqualTo("active");
        await Assert.That(body.GetProperty("suggestionsStatus").GetString()).IsEqualTo("failed");
        await Assert.That(body.GetProperty("suggestionsError").GetString()).IsEqualTo("model_unavailable");
        await Assert.That(body.GetProperty("suggestedQuestions").GetArrayLength()).IsEqualTo(0);
        await Assert.That(search.GetProperty("outcome").GetString()).IsEqualTo("sufficient");
        await Assert.That(await app.RunStatusAsync(doc.RunId)).IsEqualTo("Succeeded");
    }

    // AC-3
    [Test]
    public async Task DemoMode_skipsTheStepWithoutCallingTheModel()
    {
        var chat = new ScriptedChatClient();
        var settings = new Dictionary<string, string>(On) { ["Chat:Provider"] = "Demo" };
        await using var app = await IngestionHarness.StartAsync(postgres, generator: Vectors(), settings: settings, chat: chat);
        var doc = await ImportIndexedAsync(app, Handbook);
        await WaitForSuggestionsAsync(app, doc.VersionId);
        using var owner = app.CreateClientAs(Persona.ContentOwner);

        var body = await DocumentAsync(owner, doc.DocumentId);

        await Assert.That(body.GetProperty("suggestionsStatus").GetString()).IsEqualTo("skipped");
        await Assert.That(chat.SuggestionCalls.Count).IsEqualTo(0);
        // Nothing ran, so the stage log ends at "completed" as before.
        await Assert.That((await app.EventsAsync(doc.RunId))[^1]).IsEqualTo("Completed/Completed");
    }

    // AC-5
    [Test]
    public async Task OwnerEdit_isStoredAsEdited_survivesReprocess_andIsGuardedByTheETag()
    {
        var chat = new ScriptedChatClient();
        await using var app = await IngestionHarness.StartAsync(postgres, generator: Vectors(), settings: On, chat: chat);
        var doc = await ImportIndexedAsync(app, Handbook);
        await WaitForSuggestionsAsync(app, doc.VersionId);
        using var owner = app.CreateClientAs(Persona.ContentOwner);
        using var stranger = app.CreateClientAs(Persona.ContentOwner, subject: "someone-else");
        using var current = await owner.GetAsync($"/api/documents/{doc.DocumentId}");
        var etag = current.Headers.ETag!.Tag;

        using var stale = await PutSuggestionsAsync(owner, doc.DocumentId, "\"999\"", "Q?");
        using var missing = await PutSuggestionsAsync(owner, doc.DocumentId, null, "Q?");
        using var notOwner = await PutSuggestionsAsync(stranger, doc.DocumentId, etag, "Q?");
        using var edited = await PutSuggestionsAsync(owner, doc.DocumentId, etag, "  Who books alpha   travel? ", "What do beta expenses need?");

        await Assert.That(stale.StatusCode).IsEqualTo(HttpStatusCode.PreconditionFailed);
        await Assert.That(missing.StatusCode).IsEqualTo((HttpStatusCode)428);
        await Assert.That(notOwner.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(edited.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(edited.Headers.ETag!.Tag).IsNotEqualTo(etag);
        var body = await JsonAsync(edited);
        await Assert.That(body.GetProperty("suggestionsStatus").GetString()).IsEqualTo("edited");
        await Assert.That(body.GetProperty("suggestedQuestions")[0].GetString()).IsEqualTo("Who books alpha travel?");

        using var reprocess = await PostActionAsync(owner, doc.DocumentId, "reprocess");
        var runId = (await JsonAsync(reprocess)).GetProperty("ingestionRunId").GetGuid();
        await app.WaitForRunAsync(runId, "Succeeded");
        var after = await DocumentAsync(owner, doc.DocumentId);

        await Assert.That(after.GetProperty("latestVersion").GetProperty("number").GetInt32()).IsEqualTo(2);
        await Assert.That(after.GetProperty("suggestionsStatus").GetString()).IsEqualTo("edited");
        await Assert.That(after.GetProperty("suggestedQuestions").GetArrayLength()).IsEqualTo(2);
        // The first version asked once; the reprocessed one kept the owner's questions and did not ask again.
        await Assert.That(chat.SuggestionCalls.Count).IsEqualTo(1);
    }

    // AC-6
    [Test]
    [Arguments(4, 10)]
    [Arguments(1, 201)]
    public async Task Edit_withTooManyOrTooLongQuestions_isAValidationError(int count, int length)
    {
        await using var app = await IngestionHarness.StartAsync(postgres, generator: Vectors(), settings: On);
        var doc = await ImportIndexedAsync(app, Handbook);
        using var owner = app.CreateClientAs(Persona.ContentOwner);
        using var current = await owner.GetAsync($"/api/documents/{doc.DocumentId}");

        using var response = await PutSuggestionsAsync(
            owner, doc.DocumentId, current.Headers.ETag!.Tag, [.. Enumerable.Range(0, count).Select(i => new string('q', length - 1) + i % 10)]);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await CodeAsync(response)).IsEqualTo("validation.failed");
    }

    // AC-4
    [Test]
    public async Task Suggestions_leaveOutOtherUsersPrivateDocuments_andShowThemToTheirOwner()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var seeder = new CorpusSeeder(app.Db);
        var shared = await seeder.AddDocumentAsync(new SeedDocument("Shared", "o", Passages: 1));
        var hidden = await seeder.AddDocumentAsync(new SeedDocument("Hidden", "o", Visibility: "Private", CreatedBy: "private-owner", Passages: 1));
        await SetQuestionsAsync(app, shared.VersionId, "Ready", "Shared question?");
        await SetQuestionsAsync(app, hidden.VersionId, "Edited", "Private question?");
        using var seeker = app.CreateClientAs(Persona.KnowledgeSeeker, subject: "seeker");
        using var privateOwner = app.CreateClientAs(Persona.KnowledgeSeeker, subject: "private-owner");

        var (status, forSeeker) = await seeker.GetJsonAsync("/api/documents/suggestions?count=12");
        var (_, forOwner) = await privateOwner.GetJsonAsync("/api/documents/suggestions");
        var (badStatus, _) = await seeker.GetJsonAsync("/api/documents/suggestions?count=13");

        await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Questions(forSeeker)).IsEquivalentTo(["Shared question?"]);
        await Assert.That(Questions(forOwner)).IsEquivalentTo(["Shared question?", "Private question?"]);
        await Assert.That(forSeeker[0].GetProperty("documentTitle").GetString()).IsEqualTo("Shared");
        await Assert.That(badStatus).IsEqualTo(HttpStatusCode.BadRequest);
    }

    // AC-7
    [Test]
    public async Task Summary_countsReadableActiveDocumentsPerLanguage_andListsTheFiveMostRecent()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var seeder = new CorpusSeeder(app.Db);
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 4; i++)
        {
            await seeder.AddDocumentAsync(new SeedDocument($"English {i}", "o", UpdatedAt: start.AddDays(i)));
        }

        await seeder.AddDocumentAsync(new SeedDocument("Français 1", "o", Language: "fr", UpdatedAt: start.AddDays(10)));
        await seeder.AddDocumentAsync(new SeedDocument("Français 2", "o", Language: "fr", UpdatedAt: start.AddDays(11)));
        await seeder.AddDocumentAsync(new SeedDocument("Archived", "o", Archived: true, UpdatedAt: start.AddDays(20)));
        await seeder.AddDocumentAsync(new SeedDocument("Private", "o", Visibility: "Private", CreatedBy: "else", UpdatedAt: start.AddDays(21)));
        using var seeker = app.CreateClientAs(Persona.KnowledgeSeeker, subject: "seeker");

        var (_, summary) = await seeker.GetJsonAsync("/api/documents/summary");

        await Assert.That(summary.GetProperty("activeDocuments").GetInt32()).IsEqualTo(6);
        var languages = summary.GetProperty("byLanguage").EnumerateArray()
            .ToDictionary(l => l.GetProperty("language").GetString()!, l => l.GetProperty("count").GetInt32());
        await Assert.That(languages["en"]).IsEqualTo(4);
        await Assert.That(languages["fr"]).IsEqualTo(2);
        string[] recent = [.. summary.GetProperty("recent").EnumerateArray().Select(r => r.GetProperty("title").GetString()!)];
        await Assert.That(recent.SequenceEqual(["Français 2", "Français 1", "English 3", "English 2", "English 1"])).IsTrue();
        await Assert.That(summary.GetProperty("lastIngestedAt").ValueKind).IsEqualTo(JsonValueKind.String);
    }

    private static Task SetQuestionsAsync(IngestionHarness app, Guid versionId, string status, params string[] questions) =>
        app.ExecuteAsync(
            "UPDATE documents.document_versions SET suggested_questions = $1, suggestions_status = $2 WHERE id = $3", questions, status, versionId);

    private static string[] Questions(JsonElement list) => [.. list.EnumerateArray().Select(s => s.GetProperty("question").GetString()!)];
}
