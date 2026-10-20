using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using RagAssistant.Integration.Tests.Fixtures;
using static RagAssistant.Integration.Tests.Documents.LifecycleHelpers;

namespace RagAssistant.Integration.Tests.Documents;

// Spec 018, AC-1 to AC-7: the document language is detected at ingestion unless the owner chose it.
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerTestSession)]
[ParallelLimiter<IngestionParallelLimit>]
public sealed class DocumentLanguageTests(PostgresFixture postgres)
{
    private const string EnglishBody =
        "Employees who travel for work must book their trips through the approved agency. The manager approves each request "
        + "before it is booked, and any expense above the limit needs a written exception from the finance team.";

    private const string FrenchBody =
        "Les salariés qui se déplacent pour le travail doivent réserver leurs voyages auprès de l'agence agréée. Le responsable "
        + "approuve chaque demande avant la réservation, et toute dépense au-delà du plafond exige une dérogation écrite de la direction.";

    [Test]
    public async Task Import_withoutLanguage_englishText_detectsEnglish()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var doc = await ImportAsync(app, EnglishBody, language: null);
        await app.WaitForRunAsync(doc.RunId, "Succeeded");

        var json = await GetAsync(app, doc.DocumentId);
        var version = await ActiveVersionAsync(app, doc.DocumentId);

        await Assert.That(json.GetProperty("language").GetString()).IsEqualTo("en");
        await Assert.That(json.GetProperty("languageSource").GetString()).IsEqualTo("detect");
        await Assert.That(json.GetProperty("detectedLanguage").GetString()).IsEqualTo("en");
        await Assert.That(json.GetProperty("languageDetection").GetString()).IsEqualTo("confident");
        await Assert.That(version.GetProperty("detectedLanguage").GetString()).IsEqualTo("en");
        await Assert.That(version.GetProperty("languageDetection").GetString()).IsEqualTo("confident");
        // Nothing changed, so the ETag a client already holds stays valid.
        await Assert.That(json.GetProperty("revision").GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task Import_withoutLanguage_frenchText_becomesFrenchAndChangesETag()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var doc = await ImportAsync(app, FrenchBody, language: null);
        await app.WaitForRunAsync(doc.RunId, "Succeeded");

        using var client = app.CreateClientAs(Persona.KnowledgeSeeker);
        var response = await client.GetAsync($"/api/documents/{doc.DocumentId}");
        var json = await JsonAsync(response);

        await Assert.That(json.GetProperty("language").GetString()).IsEqualTo("fr");
        await Assert.That(json.GetProperty("languageSource").GetString()).IsEqualTo("detect");
        await Assert.That(response.Headers.ETag?.Tag).IsEqualTo("\"2\"");
    }

    [Test]
    public async Task Import_withExplicitEnglish_frenchText_keepsOwnerChoiceAndRecordsDetection()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var doc = await ImportAsync(app, FrenchBody, language: "en");
        await app.WaitForRunAsync(doc.RunId, "Succeeded");

        var json = await GetAsync(app, doc.DocumentId);

        await Assert.That(json.GetProperty("language").GetString()).IsEqualTo("en");
        await Assert.That(json.GetProperty("languageSource").GetString()).IsEqualTo("owner");
        await Assert.That(json.GetProperty("detectedLanguage").GetString()).IsEqualTo("fr");
        await Assert.That(json.GetProperty("languageDetection").GetString()).IsEqualTo("confident");
        await Assert.That(json.GetProperty("revision").GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task Import_detectWithShortText_isUncertainAndKeepsLanguage()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var doc = await ImportAsync(app, "Les congés payés de la société.", language: "detect");
        await app.WaitForRunAsync(doc.RunId, "Succeeded");

        var json = await GetAsync(app, doc.DocumentId);

        await Assert.That(json.GetProperty("language").GetString()).IsEqualTo("en");
        await Assert.That(json.GetProperty("languageSource").GetString()).IsEqualTo("detect");
        await Assert.That(json.GetProperty("languageDetection").GetString()).IsEqualTo("uncertain");
    }

    [Test]
    public async Task Put_detectOnOwnerChosenDocument_appliesConfidentDetection()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var doc = await ImportAsync(app, FrenchBody, language: "en");
        await app.WaitForRunAsync(doc.RunId, "Succeeded");
        using var owner = app.CreateClientAs(Persona.ContentOwner);

        var response = await PutAsync(owner, doc.DocumentId, Body("detect"), "\"1\"");
        var json = await JsonAsync(response);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(json.GetProperty("language").GetString()).IsEqualTo("fr");
        await Assert.That(json.GetProperty("languageSource").GetString()).IsEqualTo("detect");
    }

    [Test]
    public async Task Put_explicitLanguage_makesItTheOwnerChoice()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        var doc = await ImportAsync(app, EnglishBody, language: null);
        await app.WaitForRunAsync(doc.RunId, "Succeeded");
        using var owner = app.CreateClientAs(Persona.ContentOwner);

        var json = await JsonAsync(await PutAsync(owner, doc.DocumentId, Body("DE"), "\"1\""));

        await Assert.That(json.GetProperty("language").GetString()).IsEqualTo("de");
        await Assert.That(json.GetProperty("languageSource").GetString()).IsEqualTo("owner");
    }

    [Test]
    [Arguments("xyz")]
    [Arguments("e1")]
    [Arguments("detection")]
    public async Task ImportAndPut_invalidLanguage_return400ForLanguage(string language)
    {
        await using var app = await IngestionHarness.StartAsync(postgres);
        using var owner = app.CreateClientAs(Persona.ContentOwner);
        using var form = DocumentUploads.Form(
            "policy.md", Encoding.UTF8.GetBytes(IngestionHarness.UniqueMarkdown()), DocumentUploads.UniqueTitle(), "Finance", ("language", language));

        var import = await owner.PostAsync("/api/documents", form);
        var doc = await ImportIndexedAsync(app);
        var put = await PutAsync(owner, doc.DocumentId, Body(language), "\"1\"");

        await Assert.That(import.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await ErrorFieldsAsync(import)).Contains("language");
        await Assert.That(put.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await ErrorFieldsAsync(put)).Contains("language");
    }

    [Test]
    public async Task Migration_existingDocuments_defaultToDetection()
    {
        await using var app = await IngestionHarness.StartAsync(postgres);

        var columnDefault = await app.ScalarAsync<string>(
            "SELECT column_default FROM information_schema.columns "
            + "WHERE table_schema = 'documents' AND table_name = 'documents' AND column_name = 'language_source'");

        await Assert.That(columnDefault).StartsWith("'Detect'");
    }

    private static async Task<string[]> ErrorFieldsAsync(HttpResponseMessage response) =>
        [.. (await JsonAsync(response)).GetProperty("errors").EnumerateObject().Select(p => p.Name.ToLowerInvariant())];

    private static object Body(string language) => new
    {
        title = DocumentUploads.UniqueTitle(),
        ownerName = "Finance",
        language,
        tags = Array.Empty<string>(),
        effectiveDate = (string?)null,
        source = (string?)null,
    };

    private static async Task<ImportedDocument> ImportAsync(IngestionHarness app, string body, string? language)
    {
        using var client = app.CreateClientAs(Persona.ContentOwner);
        (string, string)[] extra = language is null ? [] : [("language", language)];
        using var form = DocumentUploads.Form(
            "policy.md", Encoding.UTF8.GetBytes($"# {Guid.NewGuid()}\n\n{body}\n"), DocumentUploads.UniqueTitle(), "Finance", extra);
        var response = await client.PostAsync("/api/documents", form);
        response.EnsureSuccessStatusCode();
        var json = await JsonAsync(response);
        return new(json.GetProperty("documentId").GetGuid(), json.GetProperty("versionId").GetGuid(), json.GetProperty("ingestionRunId").GetGuid());
    }

    private static async Task<JsonElement> GetAsync(IngestionHarness app, Guid documentId)
    {
        using var client = app.CreateClientAs(Persona.KnowledgeSeeker);
        return await client.GetFromJsonAsync<JsonElement>($"/api/documents/{documentId}");
    }

    private static async Task<JsonElement> ActiveVersionAsync(IngestionHarness app, Guid documentId)
    {
        using var client = app.CreateClientAs(Persona.KnowledgeSeeker);
        var json = await client.GetFromJsonAsync<JsonElement>($"/api/documents/{documentId}/versions");
        return json.GetProperty("items").EnumerateArray().Single(v => v.GetProperty("isActive").GetBoolean());
    }
}
