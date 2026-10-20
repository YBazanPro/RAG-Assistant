using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;

namespace RagAssistant.Evaluation.Api;

/// <summary>The chat, diagnostic and admin calls of a run (spec 018).</summary>
internal sealed partial class RagAssistantApi
{
    public async Task<Guid> CreateConversationAsync(string user, string tag, CancellationToken cancellationToken)
    {
        var created = (await SendAsync<ConversationCreated>(user, new HttpRequestMessage(HttpMethod.Post, "api/chat/conversations"), cancellationToken))
            .Require("create a conversation");
        // Spec 018: every harness conversation searches the sample corpus only, whatever else the database holds.
        var scope = new HttpRequestMessage(HttpMethod.Put, $"api/chat/conversations/{created.Id}/scope")
        {
            Content = JsonContent.Create(new { documentIds = Array.Empty<Guid>(), tags = new[] { tag } }, options: Json),
        };
        (await SendAsync<JsonElement>(user, scope, cancellationToken)).Require("scope the conversation");
        return created.Id;
    }

    /// <summary>Asks one question and reads its answer stream to the end.</summary>
    public async Task<StreamedAnswer> AskAsync(string user, Guid conversationId, string question, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/chat/conversations/{conversationId}/messages")
        {
            Content = JsonContent.Create(new { question }, options: Json),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetAsync(user, cancellationToken));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var problem = await response.Content.ReadAsStringAsync(cancellationToken);
            return StreamedAnswer.Rejected($"{(int)response.StatusCode} {problem}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var answer = new StreamedAnswer();
        var text = new StringBuilder();
        await foreach (var item in SseParser.Create(stream).EnumerateAsync(cancellationToken))
        {
            switch (item.EventType)
            {
                case "question.accepted":
                    answer.AssistantMessageId = JsonSerializer.Deserialize<QuestionAccepted>(item.Data, Json)!.AssistantMessageId;
                    break;
                case "retrieval.started":
                    answer.RetrievalQuery = JsonSerializer.Deserialize<RetrievalStarted>(item.Data, Json)!.Query;
                    break;
                case "retrieval.completed":
                    answer.Retrieval = JsonSerializer.Deserialize<RetrievalCompleted>(item.Data, Json);
                    break;
                case "answer.delta":
                    text.Append(JsonSerializer.Deserialize<AnswerDelta>(item.Data, Json)!.Text);
                    break;
                case "answer.completed":
                    answer.Completed = JsonSerializer.Deserialize<AnswerCompleted>(item.Data, Json);
                    break;
                case "error":
                    answer.Error = JsonSerializer.Deserialize<StreamError>(item.Data, Json);
                    break;
            }
        }

        answer.Text = text.ToString();
        return answer;
    }

    /// <summary>
    /// The trace of an answer: requests are listed newest first, so the harness, which asks one question at a time, finds it on
    /// the first page; the detail confirms it by the assistant message id.
    /// </summary>
    public async Task<RequestDetail?> FindTraceAsync(string user, Guid assistantMessageId, string question, CancellationToken cancellationToken)
    {
        var list = (await SendAsync<RequestPage>(user, new HttpRequestMessage(HttpMethod.Get, "api/admin/chat/requests?pageSize=10"), cancellationToken))
            .Require("list request traces");
        foreach (var item in list.Items.Where(i => i.QuestionText == question))
        {
            var detail = (await SendAsync<RequestDetail>(user, new HttpRequestMessage(HttpMethod.Get, $"api/admin/chat/requests/{item.Id}"), cancellationToken))
                .Require("read a request trace");
            if (detail.AssistantMessageId == assistantMessageId)
            {
                return detail;
            }
        }

        return null;
    }

    public async Task<SearchDiagnostics> SearchCandidatesAsync(string user, string query, string tag, CancellationToken cancellationToken)
    {
        var url = $"api/documents/search?q={Uri.EscapeDataString(query)}&tags={Uri.EscapeDataString(tag)}&candidates=true";
        return (await SendAsync<SearchDiagnostics>(user, new HttpRequestMessage(HttpMethod.Get, url), cancellationToken)).Require("search with candidates");
    }

    /// <summary>One retrieval variant of spec 019 through the maintainer overrides, timed from the harness (query embedding included).</summary>
    public async Task<(SearchDiagnostics Result, int Ms)> SearchVariantAsync(
        string user, string query, string tag, int topK, string mode, bool rerank, string? queryLanguage, CancellationToken cancellationToken)
    {
        var url = $"api/documents/search?q={Uri.EscapeDataString(query)}&tags={Uri.EscapeDataString(tag)}&topK={topK}&mode={mode}&rerank={(rerank ? "true" : "false")}"
            + (queryLanguage is null ? "" : $"&queryLanguage={queryLanguage}");
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var result = (await SendAsync<SearchDiagnostics>(user, new HttpRequestMessage(HttpMethod.Get, url), cancellationToken)).Require($"search ({mode}, rerank {rerank})");
        return (result, (int)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    public async Task<IReadOnlyList<PassageItem>> PassagesAsync(string user, Guid documentId, int version, CancellationToken cancellationToken)
    {
        var passages = new List<PassageItem>();
        for (var page = 1; ; page++)
        {
            var url = $"api/documents/{documentId}/versions/{version}/passages?pageSize=100&page={page}";
            var body = (await SendAsync<PassagePage>(user, new HttpRequestMessage(HttpMethod.Get, url), cancellationToken)).Require("list passages");
            passages.AddRange(body.Items);
            if (page * body.PageSize >= body.TotalCount)
            {
                return passages;
            }
        }
    }

    public async Task<JsonElement> ChatConfigurationAsync(string user, CancellationToken cancellationToken) =>
        (await SendAsync<JsonElement>(user, new HttpRequestMessage(HttpMethod.Get, "api/admin/chat/configuration"), cancellationToken))
            .Require("read the chat configuration");

    public async Task<JsonElement> DocumentsConfigurationAsync(string user, CancellationToken cancellationToken) =>
        (await SendAsync<JsonElement>(user, new HttpRequestMessage(HttpMethod.Get, "api/admin/documents/configuration"), cancellationToken))
            .Require("read the documents configuration");
}

/// <summary>What one answer stream carried.</summary>
internal sealed class StreamedAnswer
{
    public Guid AssistantMessageId { get; set; }

    public string? RetrievalQuery { get; set; }

    public RetrievalCompleted? Retrieval { get; set; }

    public AnswerCompleted? Completed { get; set; }

    public StreamError? Error { get; set; }

    public string Text { get; set; } = "";

    /// <summary>Set when the question was refused before streaming (400, 409...).</summary>
    public string? RejectedWith { get; private set; }

    public static StreamedAnswer Rejected(string reason) => new() { RejectedWith = reason };

    /// <summary>complete, partial, refused, failed, cancelled, or error when nothing usable came back.</summary>
    public string Status => Completed?.Status ?? (Error is not null ? "failed" : "error");
}

internal sealed record ConversationCreated(Guid Id);

internal sealed record QuestionAccepted(Guid ConversationId, Guid UserMessageId, Guid AssistantMessageId);

internal sealed record RetrievalStarted(string Query);

internal sealed record AnswerSource(int Marker, Guid PassageId, Guid DocumentId, string DocumentTitle, int VersionNumber, double Score, string? Language);

internal sealed record RetrievalCompleted(string Outcome, IReadOnlyList<AnswerSource> Sources, double MinScore, double ConfidentScore, int? RetrievalMs, double? BestScore);

internal sealed record AnswerDelta(string Text);

internal sealed record Citation(int Marker, Guid PassageId, Guid DocumentId, string? Language);

internal sealed record AnswerCompleted(string Status, string? RefusalReason, IReadOnlyList<Citation> Citations);

internal sealed record StreamError(string Code, string Message, bool Retryable);

internal sealed record RequestPage(IReadOnlyList<RequestListItem> Items, int Page, int PageSize, int TotalCount);

internal sealed record RequestListItem(Guid Id, string QuestionText, string FinalStatus);

internal sealed record RequestDetail(
    Guid Id,
    Guid AssistantMessageId,
    RequestQuestion Question,
    RequestRewrite Rewrite,
    RequestRetrieval Retrieval,
    RequestGeneration Generation);

internal sealed record RequestQuestion(string Text, string? DetectedLanguage, string? LanguageSource);

internal sealed record RequestRewrite(bool Used, string? RetrievalQuery, string? FallbackReason, string? PromptVersion);

/// <param name="Mode">Spec 019: <c>vector</c> or <c>hybrid</c>; null on traces written before it.</param>
internal sealed record RequestRetrieval(
    string? EvidenceOutcome, double? MinScore, double? ConfidentScore, double? DuplicateOverlap, double? BestScore, string? EmbeddingModel, int? Ms,
    string? Mode = null, string? Rerank = null);

internal sealed record RequestGeneration(string? ChatModel, string? PromptVersion, int? FirstTokenMs, int TotalMs, string? AnswerLanguage);

/// <param name="Passages">The kept passages, best first: what the chat would cite from.</param>
internal sealed record SearchDiagnostics(
    string Outcome, int TopK, IReadOnlyList<SearchCandidate>? Candidates, IReadOnlyList<SearchPassage>? Passages = null,
    string? Mode = null, string? Rerank = null, int? RerankMs = null);

internal sealed record SearchPassage(int Rank, Guid PassageId, double Score);

internal sealed record SearchCandidate(Guid PassageId, Guid DocumentId, string Language, double Score, string? Dropped);

internal sealed record PassagePage(IReadOnlyList<PassageItem> Items, int Page, int PageSize, int TotalCount);

internal sealed record PassageItem(Guid Id, int Position, string Text);
