namespace RagAssistant.Modules.Chat.Domain;

/// <summary>
/// The decisions behind one answer attempt, for diagnosis (roadmap 4.1, write side). Never holds passage text or the prompt
/// (ADR 0004 D4); the answer text stays on the message.
/// </summary>
internal sealed class RequestTrace
{
    public Guid Id { get; set; }

    public Guid ConversationId { get; set; }

    public Guid UserMessageId { get; set; }

    public Guid AssistantMessageId { get; set; }

    public int Attempt { get; set; }

    public required string OwnerSub { get; set; }

    public required string QuestionText { get; set; }

    /// <summary>What retrieval searched for: the standalone question, or the original one (spec 014).</summary>
    public string? RetrievalQuery { get; set; }

    public bool RewriteUsed { get; set; }

    public RewriteFallback? RewriteFallbackReason { get; set; }

    public int? RewriteMs { get; set; }

    public long? RewriteInputTokens { get; set; }

    public long? RewriteOutputTokens { get; set; }

    public string? RewritePromptVersion { get; set; }

    public string? FilterLanguage { get; set; }

    /// <summary>The language the question is written in (spec 018); null on traces written before it.</summary>
    public string? QuestionLanguage { get; set; }

    public QuestionLanguageSource? QuestionLanguageSource { get; set; }

    /// <summary>The language the answer prompt named; null when the model was never called.</summary>
    public string? AnswerLanguage { get; set; }

    /// <summary>The answer language the rewrite said the user asked for; null when none or no rewrite ran.</summary>
    public string? RequestedLanguage { get; set; }

    public string[] FilterTags { get; set; } = [];

    public Guid[] FilterDocumentIds { get; set; } = [];

    /// <summary>Chosen documents retrieval could not use (archived, deleted, not indexed); spec 015.</summary>
    public Guid[] UnavailableDocumentIds { get; set; } = [];

    public string? OtelTraceId { get; set; }

    /// <summary>Null when retrieval itself failed.</summary>
    public string? EvidenceOutcome { get; set; }

    public double? MinScore { get; set; }

    public double? ConfidentScore { get; set; }

    public double? DuplicateOverlap { get; set; }

    /// <summary>The best score among the candidates before the evidence rules; shows how close a refused question came (null when nothing matched).</summary>
    public double? BestScore { get; set; }

    public string? EmbeddingModel { get; set; }

    /// <summary>JSON array of <see cref="RetrievedPassageTrace"/>, stored as <c>jsonb</c>.</summary>
    public string RetrievedPassages { get; set; } = "[]";

    /// <summary>Null when the model was never called.</summary>
    public string? ChatModel { get; set; }

    public string? PromptVersion { get; set; }

    public long? InputTokens { get; set; }

    public long? OutputTokens { get; set; }

    public bool SentinelHit { get; set; }

    public int StrippedMarkerCount { get; set; }

    public AnswerStatus FinalStatus { get; set; }

    public RefusalReason? RefusalReason { get; set; }

    public string? ErrorCode { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public int? RetrievalMs { get; set; }

    public int? FirstTokenMs { get; set; }

    public int TotalMs { get; set; }
}

/// <remarks>The document fields (spec 015) are what the activity panel shows; traces written before it lack them. No passage text is stored.</remarks>
internal sealed record RetrievedPassageTrace(
    Guid PassageId,
    Guid VersionId,
    double Score,
    int Rank,
    int Marker,
    bool Cited,
    Guid? DocumentId = null,
    string? DocumentTitle = null,
    int? VersionNumber = null,
    string[]? SectionPath = null,
    int? PageStart = null,
    int? PageEnd = null);
