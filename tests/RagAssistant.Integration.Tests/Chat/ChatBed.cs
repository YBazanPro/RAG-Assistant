using System.Net;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Text.Json;
using RagAssistant.Integration.Tests.Documents;
using RagAssistant.Integration.Tests.Fixtures;

namespace RagAssistant.Integration.Tests.Chat;

/// <summary>
/// An application on its own database with a scripted chat model and controlled vectors (as <see cref="RetrievalBed"/>): questions
/// containing "budget" embed as axis 0, so a passage seeded with <see cref="RetrievalBed.P"/> scores exactly. Evidence is
/// <c>Sufficient</c> from 0.7 and <c>LowConfidence</c> from 0.5.
/// </summary>
public sealed class ChatBed : IAsyncDisposable
{
    public const string Question = "Who approves a budget exception?";
    public const string OffCorpusQuestion = "What is the capital of Australia?";

    private static readonly TimeSpan StreamTimeout = TimeSpan.FromSeconds(30);

    private static readonly Dictionary<string, string> DefaultSettings = new()
    {
        ["Documents:Retrieval:MinScore"] = "0.5",
        ["Documents:Retrieval:ConfidentScore"] = "0.7",
        ["Documents:Retrieval:DuplicateOverlap"] = "0.8",
    };

    private ChatBed(IngestionHarness app)
    {
        App = app;
        Seeder = new CorpusSeeder(app.Db);
    }

    public IngestionHarness App { get; }

    public ScriptedChatClient Chat => App.Chat;

    public FakeEmbeddingGenerator Generator => App.Generator;

    public CorpusSeeder Seeder { get; }

    public static async Task<ChatBed> StartAsync(
        PostgresFixture postgres, IReadOnlyDictionary<string, string>? settings = null, string? databaseName = null, ManualTimeProvider? clock = null)
    {
        var merged = new Dictionary<string, string>(DefaultSettings);
        foreach (var (key, value) in settings ?? new Dictionary<string, string>())
        {
            merged[key] = value;
        }

        var generator = new FakeEmbeddingGenerator();
        generator.Script("budget", FakeEmbeddingGenerator.Axis(0));
        return new ChatBed(await IngestionHarness.StartAsync(postgres, databaseName, generator, merged, clock));
    }

    /// <summary>A document whose passages score as given against <see cref="Question"/>.</summary>
    public async Task<(SeededDocument Document, Guid[] Passages)> AddAsync(string title, params SeedPassage[] passages)
    {
        var seeded = await Seeder.AddDocumentAsync(new SeedDocument(title, "owner", EmbeddingModel: RetrievalBed.Model));
        var ids = await Seeder.AddEmbeddedPassagesAsync(seeded.VersionId, passages);
        return (seeded, ids);
    }

    public HttpClient ClientFor(string user) => App.CreateClientAs(Persona.KnowledgeSeeker, subject: user);

    public async Task<(HttpClient Client, Guid ConversationId)> NewConversationAsync(string? user = null)
    {
        var client = ClientFor(user ?? ChatRequests.NewUser());
        return (client, await client.CreateConversationAsync());
    }

    /// <summary>Asks and reads the stream to its end.</summary>
    public static async Task<StreamResult> AskAsync(HttpClient client, Guid conversationId, string question = Question) =>
        await PostStreamAsync(client, $"/api/chat/conversations/{conversationId}/messages", new { question });

    public static async Task<StreamResult> RetryAsync(HttpClient client, Guid conversationId, Guid assistantMessageId) =>
        await PostStreamAsync(client, $"/api/chat/conversations/{conversationId}/messages/{assistantMessageId}/retry", null);

    public static async Task<StreamResult> PostStreamAsync(HttpClient client, string url, object? body)
    {
        using var timeout = new CancellationTokenSource(StreamTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = body is null ? null : JsonContent.Create(body) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
        {
            var text = await response.Content.ReadAsStringAsync(timeout.Token);
            var problem = text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
            return new StreamResult(response.StatusCode, [], problem);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        var events = new List<SseItem<string>>();
        await foreach (var item in SseParser.Create(stream).EnumerateAsync(timeout.Token))
        {
            events.Add(item);
        }

        return new StreamResult(response.StatusCode, events, default);
    }

    /// <summary>Asks and keeps the stream open, for tests that read part of it or disconnect.</summary>
    public static async Task<LiveAnswer> OpenAsync(HttpClient client, Guid conversationId, string question = Question)
    {
        var timeout = new CancellationTokenSource(StreamTimeout);
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/chat/conversations/{conversationId}/messages")
        {
            Content = JsonContent.Create(new { question }),
        };
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        return new LiveAnswer(response, SseParser.Create(stream).EnumerateAsync(timeout.Token).GetAsyncEnumerator(timeout.Token), timeout);
    }

    /// <summary>Status, refusal reason, error code and text of an assistant message.</summary>
    public async Task<(string? Status, string? RefusalReason, string? ErrorCode, string Text)> AnswerAsync(Guid assistantMessageId)
    {
        var rows = await App.RowsAsync("SELECT status, refusal_reason, error_code, text FROM chat.messages WHERE id = $1", assistantMessageId);
        var row = rows.Single();
        return ((string?)row[0], (string?)row[1], (string?)row[2], (string)row[3]!);
    }

    public async Task<List<object?[]>> TracesAsync(Guid assistantMessageId) =>
        await App.RowsAsync(
            "SELECT attempt, evidence_outcome, chat_model, final_status, error_code, sentinel_hit, stripped_marker_count, retrieved_passages::text, " +
            "refusal_reason, question_text FROM chat.request_traces WHERE assistant_message_id = $1 ORDER BY attempt",
            assistantMessageId);

    public async ValueTask DisposeAsync() => await App.DisposeAsync();
}

/// <summary>An answer stream being read; disposing it is the client disconnect.</summary>
public sealed class LiveAnswer(HttpResponseMessage response, IAsyncEnumerator<SseItem<string>> events, CancellationTokenSource timeout) : IAsyncDisposable
{
    public List<SseItem<string>> Received { get; } = [];

    /// <summary>Reads until an event matches, and returns it.</summary>
    public async Task<SseItem<string>> ReadUntilAsync(Func<SseItem<string>, bool> stop)
    {
        while (await events.MoveNextAsync())
        {
            Received.Add(events.Current);
            if (stop(events.Current))
            {
                return events.Current;
            }
        }

        throw new InvalidOperationException("The stream ended before the expected event.");
    }

    public async Task ReadToEndAsync()
    {
        while (await events.MoveNextAsync())
        {
            Received.Add(events.Current);
        }
    }

    /// <summary>
    /// Disconnects the way a browser does mid-read: the test server aborts the request (and so cancels <c>RequestAborted</c>) when
    /// a pending read of the response body is cancelled.
    /// </summary>
    public async Task DisconnectAsync()
    {
        var pending = events.MoveNextAsync().AsTask();
        await timeout.CancelAsync();
        try
        {
            await pending;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // Expected: the read was cancelled to disconnect.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await timeout.CancelAsync();
        try
        {
            await events.DisposeAsync();
        }
        catch (OperationCanceledException)
        {
            // Expected: the read was cancelled to disconnect.
        }

        response.Dispose();
        timeout.Dispose();
    }
}

public sealed record StreamResult(HttpStatusCode Status, IReadOnlyList<SseItem<string>> Events, JsonElement Problem)
{
    /// <summary>Event types in order, heartbeats left out.</summary>
    public string[] Types => [.. Events.Where(e => e.EventType != "heartbeat").Select(e => e.EventType)];

    public JsonElement Data(string type) => JsonDocument.Parse(Events.Single(e => e.EventType == type).Data).RootElement.Clone();

    public string Deltas => string.Concat(
        Events.Where(e => e.EventType == "answer.delta").Select(e => JsonDocument.Parse(e.Data).RootElement.GetProperty("text").GetString()));

    public Guid AssistantMessageId => Data("question.accepted").GetProperty("assistantMessageId").GetGuid();
}
