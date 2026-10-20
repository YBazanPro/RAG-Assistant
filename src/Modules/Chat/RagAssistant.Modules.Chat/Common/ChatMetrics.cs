using System.Diagnostics.Metrics;
using RagAssistant.Modules.Chat.Domain;

namespace RagAssistant.Modules.Chat.Common;

/// <summary>
/// The Chat meter (spec 017). It is recorded from the finished request trace, once per attempt, so a metric and its trace cannot
/// differ. Labels are low-cardinality on purpose: never a user, conversation, document or passage id, never question text.
/// </summary>
internal sealed class ChatMetrics
{
    public const string MeterName = "RagAssistant.Chat";
    public const string RequestsName = "ragassistant.chat.requests";
    public const string RefusalsName = "ragassistant.chat.refusals";
    public const string RetrievalResultsName = "ragassistant.chat.retrieval.results";
    public const string FirstTokenDurationName = "ragassistant.chat.first_token.duration";
    public const string RequestDurationName = "ragassistant.chat.request.duration";
    public const string ErrorsName = "ragassistant.chat.errors";

    private readonly Counter<long> _requests;
    private readonly Counter<long> _refusals;
    private readonly Histogram<long> _retrievalResults;
    private readonly Histogram<double> _firstToken;
    private readonly Histogram<double> _requestDuration;
    private readonly Counter<long> _errors;

    public ChatMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _requests = meter.CreateCounter<long>(RequestsName, description: "Answer attempts, by final status and evidence outcome.");
        _refusals = meter.CreateCounter<long>(RefusalsName, description: "Refused answers, by reason.");
        _retrievalResults = meter.CreateHistogram<long>(RetrievalResultsName, description: "Passages kept by retrieval per request, by evidence outcome.");
        _firstToken = meter.CreateHistogram<double>(FirstTokenDurationName, unit: "s", description: "Request start to the first answer token.");
        _requestDuration = meter.CreateHistogram<double>(RequestDurationName, unit: "s", description: "Request start to the end of the attempt, by final status.");
        _errors = meter.CreateCounter<long>(ErrorsName, description: "Errors per pipeline stage, including rewrite fallbacks.");
    }

    public void Record(RequestTrace trace, int keptPassages)
    {
        var status = trace.FinalStatus.ToApi();
        var outcome = trace.EvidenceOutcome ?? "none";
        // Spec 018: en, fr, or "other"; never an open-ended code, so the label stays low-cardinality.
        var language = trace.QuestionLanguage is "en" or "fr" ? trace.QuestionLanguage : "other";
        _requests.Add(1, Tag("status", status), Tag("evidence_outcome", outcome), Tag("question.language", language));
        _requestDuration.Record(trace.TotalMs / 1000d, Tag("status", status));

        if (trace.RefusalReason is { } reason)
        {
            _refusals.Add(1, Tag("reason", reason.ToApi()), Tag("question.language", language));
        }

        if (trace.EvidenceOutcome is not null)
        {
            _retrievalResults.Record(keptPassages, Tag("evidence_outcome", outcome));
        }

        if (trace.FirstTokenMs is { } firstToken)
        {
            _firstToken.Record(firstToken / 1000d);
        }

        foreach (var (stage, code) in ErrorStages.Of(trace))
        {
            _errors.Add(1, Tag("stage", stage), Tag("error_code", code));
        }
    }

    private static KeyValuePair<string, object?> Tag(string key, object value) => new(key, value);
}
