using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using RagAssistant.Integration.Tests.Fixtures;
using static RagAssistant.Integration.Tests.Documents.RetrievalBed;

namespace RagAssistant.Integration.Tests.Chat;

// Spec 018, AC-11 to AC-21d: answers follow the language of the question, or the one the user asked for.
[ClassDataSource<PostgresFixture>(Shared = SharedType.PerTestSession)]
[ParallelLimiter<IngestionParallelLimit>]
public sealed class AnswerLanguageTests(PostgresFixture postgres)
{
    // Both contain "budget", so ChatBed embeds them on the seeded axis.
    private const string FrenchQuestion = "Qui approuve une exception au budget ?";
    private const string EnglishQuestion = ChatBed.Question;

    private const string AnswerRule = "- If the user explicitly asks for another language, answer in that language instead.";

    private static string SystemText(IReadOnlyList<ChatMessage> prompt) => prompt.Single(m => m.Role == ChatRole.System).Text;

    private static Guid UserMessageId(StreamResult result) => result.Data("question.accepted").GetProperty("userMessageId").GetGuid();

    private static async Task<(string? Language, string? Source, string? Requested)> UserLanguageAsync(ChatBed bed, Guid userMessageId)
    {
        var row = (await bed.App.RowsAsync(
            "SELECT question_language, question_language_source, requested_language FROM chat.messages WHERE id = $1", userMessageId)).Single();
        return ((string?)row[0], (string?)row[1], (string?)row[2]);
    }

    private static async Task<(string? Question, string? Source, string? Answer, string? Requested, string? RewriteVersion)> TraceLanguageAsync(
        ChatBed bed, Guid assistantMessageId)
    {
        var row = (await bed.App.RowsAsync(
            "SELECT question_language, question_language_source, answer_language, requested_language, rewrite_prompt_version "
            + "FROM chat.request_traces WHERE assistant_message_id = $1 ORDER BY attempt DESC LIMIT 1", assistantMessageId)).Single();
        return ((string?)row[0], (string?)row[1], (string?)row[2], (string?)row[3], (string?)row[4]);
    }

    // AC-11, AC-21a
    [Test]
    public async Task FrenchQuestion_promptNamesFrench_andLanguageIsStoredAsDetected()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Budget Exception Policy", P(0.9));
        var (client, id) = await bed.NewConversationAsync();

        var result = await ChatBed.AskAsync(client, id, FrenchQuestion);

        var system = SystemText(bed.Chat.Calls.Single());
        await Assert.That(system).Contains("- Answer in French.");
        await Assert.That(system).Contains(AnswerRule);
        await Assert.That(await UserLanguageAsync(bed, UserMessageId(result))).IsEqualTo(("fr", "Detected", (string?)null));
        var trace = await TraceLanguageAsync(bed, result.AssistantMessageId);
        await Assert.That(trace.Question).IsEqualTo("fr");
        await Assert.That(trace.Source).IsEqualTo("Detected");
        await Assert.That(trace.Answer).IsEqualTo("fr");
    }

    // AC-12
    [Test]
    public async Task EnglishQuestion_promptNamesEnglish()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Budget Exception Policy", P(0.9));
        var (client, id) = await bed.NewConversationAsync();

        await ChatBed.AskAsync(client, id, EnglishQuestion);

        var system = SystemText(bed.Chat.Calls.Single());
        await Assert.That(system).Contains("- Answer in English.");
        await Assert.That(system).Contains(AnswerRule);
    }

    // AC-13
    [Test]
    public async Task UncertainFollowUp_keepsTheConversationsPreviousLanguage()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Budget Exception Policy", P(0.9));
        var (client, id) = await bed.NewConversationAsync();
        await ChatBed.AskAsync(client, id, FrenchQuestion);

        var next = await ChatBed.AskAsync(client, id, "OK budget ?");

        await Assert.That(await UserLanguageAsync(bed, UserMessageId(next))).IsEqualTo(("fr", "Conversation", (string?)null));
    }

    // AC-14
    [Test]
    public async Task UnclassifiableFirstQuestion_defaultsToEnglish()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Budget Exception Policy", P(0.9));
        var (client, id) = await bed.NewConversationAsync();

        var result = await ChatBed.AskAsync(client, id, "BX-204 budget?");

        await Assert.That(await UserLanguageAsync(bed, UserMessageId(result))).IsEqualTo(("en", "Default", (string?)null));
        await Assert.That(SystemText(bed.Chat.Calls.Single())).Contains("- Answer in English.");
    }

    // AC-15, AC-21d
    [Test]
    public async Task FrenchFollowUp_rewriteAsksForAFrenchQuestionAndTheRequestedLanguage()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Budget Exception Policy", P(0.9));
        var (client, id) = await bed.NewConversationAsync();
        await ChatBed.AskAsync(client, id, FrenchQuestion);

        var result = await ChatBed.AskAsync(client, id, "Et qui approuve au-delà de ce montant pour le budget ?");

        var rewrite = SystemText(bed.Chat.RewriteCalls.Single());
        await Assert.That(rewrite).Contains("- Write the question in French.");
        await Assert.That(rewrite).Contains("requestedLanguage");
        var trace = await TraceLanguageAsync(bed, result.AssistantMessageId);
        await Assert.That(trace.RewriteVersion).IsEqualTo("rw.018.1");
        await Assert.That(trace.Requested).IsNull();
    }

    // AC-16
    [Test]
    public async Task FrenchQuestion_citesAnEnglishPassage_andEveryCitationCarriesItsLanguage()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Budget Exception Policy", P(0.9));
        var (client, id) = await bed.NewConversationAsync();

        var result = await ChatBed.AskAsync(client, id, FrenchQuestion);

        await Assert.That(result.Data("retrieval.completed").GetProperty("sources")[0].GetProperty("language").GetString()).IsEqualTo("en");
        var citation = result.Data("answer.completed").GetProperty("citations")[0];
        await Assert.That(citation.GetProperty("language").GetString()).IsEqualTo("en");
        await Assert.That(await bed.App.ScalarAsync<string>(
            "SELECT language FROM chat.citation_snapshots WHERE message_id = $1", result.AssistantMessageId)).IsEqualTo("en");
        var conversation = await client.GetFromJsonAsync<JsonElement>($"/api/chat/conversations/{id}");
        var messages = conversation.GetProperty("messages").EnumerateArray().ToList();
        await Assert.That(messages[0].GetProperty("questionLanguage").GetString()).IsEqualTo("fr");
        await Assert.That(messages[1].GetProperty("citations")[0].GetProperty("language").GetString()).IsEqualTo("en");
    }

    // AC-18
    [Test]
    public async Task StoredAnswer_activityAndAdminDetailShowTheLanguages()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Budget Exception Policy", P(0.9));
        var (client, id) = await bed.NewConversationAsync();
        var result = await ChatBed.AskAsync(client, id, FrenchQuestion);
        using var admin = bed.App.CreateClientAs(Persona.Maintainer);

        var activity = await client.GetFromJsonAsync<JsonElement>($"/api/chat/conversations/{id}/messages/{result.AssistantMessageId}/activity");
        var list = await admin.GetFromJsonAsync<JsonElement>("/api/admin/chat/requests");
        var traceId = list.GetProperty("items")[0].GetProperty("id").GetGuid();
        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/chat/requests/{traceId}");

        await Assert.That(activity.GetProperty("questionLanguage").GetString()).IsEqualTo("fr");
        await Assert.That(activity.GetProperty("questionLanguageSource").GetString()).IsEqualTo("detected");
        await Assert.That(detail.GetProperty("question").GetProperty("detectedLanguage").GetString()).IsEqualTo("fr");
        await Assert.That(detail.GetProperty("question").GetProperty("languageSource").GetString()).IsEqualTo("detected");
        await Assert.That(detail.GetProperty("generation").GetProperty("answerLanguage").GetString()).IsEqualTo("fr");
    }

    // AC-19
    [Test]
    public async Task AdminList_filteredByQuestionLanguage_returnsOnlyThoseRequests()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Budget Exception Policy", P(0.9));
        var (client, id) = await bed.NewConversationAsync();
        await ChatBed.AskAsync(client, id, FrenchQuestion);
        await ChatBed.AskAsync(client, id, EnglishQuestion);
        using var admin = bed.App.CreateClientAs(Persona.Maintainer);

        var french = await admin.GetFromJsonAsync<JsonElement>("/api/admin/chat/requests?questionLanguage=FR");
        var invalid = await admin.GetAsync("/api/admin/chat/requests?questionLanguage=french");

        var items = french.GetProperty("items").EnumerateArray().ToList();
        await Assert.That(items.Count).IsEqualTo(1);
        await Assert.That(items[0].GetProperty("questionText").GetString()).IsEqualTo(FrenchQuestion);
        await Assert.That((int)invalid.StatusCode).IsEqualTo(400);
    }

    // AC-20
    [Test]
    public async Task FrenchQuestion_requestCounterIsTaggedWithItsLanguage()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        var meterFactory = bed.App.Services.GetRequiredService<IMeterFactory>();
        var languages = new ConcurrentBag<string>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name == "ragassistant.chat.requests" && ReferenceEquals(instrument.Meter.Scope, meterFactory))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "question.language")
                {
                    languages.Add(tag.Value?.ToString() ?? "");
                }
            }
        });
        listener.Start();
        await bed.AddAsync("Budget Exception Policy", P(0.9));
        var (client, id) = await bed.NewConversationAsync();

        await ChatBed.AskAsync(client, id, FrenchQuestion);

        await Assert.That(languages.ToArray()).IsEquivalentTo(["fr"]);
    }

    // AC-21
    [Test]
    public async Task Retry_ofAQuestionStoredWithoutLanguage_detectsItFromTheText()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Budget Exception Policy", P(0.9));
        bed.Chat.ThrowAfter(0);
        var (client, id) = await bed.NewConversationAsync();
        var failed = await ChatBed.AskAsync(client, id, FrenchQuestion);
        var userMessageId = UserMessageId(failed);
        await bed.App.ExecuteAsync(
            "UPDATE chat.messages SET question_language = NULL, question_language_source = NULL WHERE id = $1", userMessageId);

        await ChatBed.RetryAsync(client, id, failed.AssistantMessageId);

        await Assert.That(await UserLanguageAsync(bed, userMessageId)).IsEqualTo(("fr", "Detected", (string?)null));
        await Assert.That(SystemText(bed.Chat.Calls[^1])).Contains("- Answer in French.");
    }

    // AC-21b
    [Test]
    public async Task FollowUp_withARequestedLanguage_answersInIt_andRecordsTheRequest()
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Budget Exception Policy", P(0.9));
        var (client, id) = await bed.NewConversationAsync();
        await ChatBed.AskAsync(client, id, "Réponds en anglais désormais : qui approuve une exception au budget ?");
        bed.Chat.ScriptRewrite("Qui approuve une exception au budget au-delà de 10 000 ?", "EN");

        var result = await ChatBed.AskAsync(client, id, "Et au-delà de 10 000 pour le budget ?");

        await Assert.That(SystemText(bed.Chat.Calls[^1])).Contains("- Answer in English.");
        await Assert.That(await UserLanguageAsync(bed, UserMessageId(result))).IsEqualTo(("fr", "Detected", "en"));
        var trace = await TraceLanguageAsync(bed, result.AssistantMessageId);
        await Assert.That(trace.Question).IsEqualTo("fr");
        await Assert.That(trace.Source).IsEqualTo("Requested");
        await Assert.That(trace.Requested).IsEqualTo("en");
        await Assert.That(trace.Answer).IsEqualTo("en");
        var activity = await client.GetFromJsonAsync<JsonElement>($"/api/chat/conversations/{id}/messages/{result.AssistantMessageId}/activity");
        await Assert.That(activity.GetProperty("questionLanguageSource").GetString()).IsEqualTo("requested");
    }

    // AC-21c
    [Test]
    [Arguments("es", "- Answer in Spanish.")]
    [Arguments("xx", "- Answer in French.")]
    [Arguments(null, "- Answer in French.")]
    public async Task FollowUp_requestedLanguageFromTheRewrite_isUsedOnlyWhenItIsAKnownLanguage(string? requested, string expected)
    {
        await using var bed = await ChatBed.StartAsync(postgres);
        await bed.AddAsync("Budget Exception Policy", P(0.9));
        var (client, id) = await bed.NewConversationAsync();
        await ChatBed.AskAsync(client, id, FrenchQuestion);
        bed.Chat.ScriptRewrite("Qui approuve une exception au budget au-delà de 10 000 ?", requested);

        var result = await ChatBed.AskAsync(client, id, "Et au-delà de 10 000 pour le budget ?");

        await Assert.That(SystemText(bed.Chat.Calls[^1])).Contains(expected);
        await Assert.That((await TraceLanguageAsync(bed, result.AssistantMessageId)).Requested).IsEqualTo(requested == "es" ? "es" : null);
    }
}
