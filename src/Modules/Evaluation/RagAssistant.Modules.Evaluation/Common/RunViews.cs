using System.Text.Json;
using RagAssistant.Modules.Evaluation.Domain;
using RagAssistant.SharedKernel.Common;

namespace RagAssistant.Modules.Evaluation.Common;

/// <summary>The numbers a list row shows (spec 020); null when the run did not measure them (demo mode has no answer metrics).</summary>
public sealed record HeadlineMetrics(
    int? Cases,
    int? Errors,
    double? RecallAt5,
    double? Mrr,
    double? BehaviourCorrectness,
    double? CitationPrecision,
    double? AnswerLanguageCorrectness,
    double? Groundedness);

public sealed record RunSummary(
    string Id,
    DateTimeOffset StartedAt,
    string? ChatModel,
    string? EmbeddingModel,
    string? JudgeModel,
    string? RetrievalMode,
    bool Rerank,
    string DatasetHash,
    string CorpusHash,
    string? GitCommit,
    bool Valid,
    DateTimeOffset UploadedAt,
    HeadlineMetrics Headline);

/// <param name="Metrics"><c>overall</c>, <c>byCategory</c>, <c>byQuestionLanguage</c>, <c>brokenCases</c>, <c>variants</c>, as the harness wrote them.</param>
/// <param name="Judge">The judge's result, or null when the run was not judged.</param>
public sealed record RunDetail(RunSummary Run, JsonElement Configuration, JsonElement Metrics, JsonElement? Judge, JsonElement Cases);

internal static class RunViews
{
    public static RunSummary Summary(EvaluationRun run)
    {
        var overall = Json.Object(Json.Parse(run.Metrics), "overall");
        var judge = run.Judge is null ? null : Json.Object(Json.Parse(run.Judge), "overall");
        return new RunSummary(
            run.Id, run.StartedAt, run.ChatModel, run.EmbeddingModel, run.JudgeModel, run.RetrievalMode, run.Rerank, run.DatasetHash, run.CorpusHash,
            run.GitCommit, run.Valid, run.UploadedAt,
            new HeadlineMetrics(
                Json.Int(overall, "cases"),
                Json.Int(overall, "errors"),
                Json.Number(overall, "recallAt5"),
                Json.Number(overall, "mrr"),
                Json.Number(overall, "behaviourCorrectness"),
                Json.Number(overall, "citationPrecision"),
                Json.Number(overall, "answerLanguageCorrectness"),
                Json.Number(judge, "groundedness")));
    }

    public static RunDetail Detail(EvaluationRun run) => new(
        Summary(run),
        Json.Parse(run.Configuration),
        Json.Parse(run.Metrics),
        run.Judge is null ? null : Json.Parse(run.Judge),
        Json.Parse(run.Cases));
}

internal static class EvaluationErrors
{
    public static readonly Error RunNotFound = Error.NotFound("evaluation.run_not_found", "The evaluation run was not found.");
}
