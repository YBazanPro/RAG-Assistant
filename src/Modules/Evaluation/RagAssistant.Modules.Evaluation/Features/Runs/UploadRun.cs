using System.Text.Json;
using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RagAssistant.Modules.Evaluation.Common;
using RagAssistant.Modules.Evaluation.Domain;
using RagAssistant.Modules.Evaluation.Persistence;
using RagAssistant.Modules.Evaluation.Persistence.Configurations;
using RagAssistant.SharedKernel.Common;

namespace RagAssistant.Modules.Evaluation.Features.Runs;

/// <summary>
/// Stores a harness report (spec 020): <c>report.json</c> as the harness writes it, optionally with the judge's result in a top-level
/// <c>judge</c> property. Idempotent by run id: the same run uploaded again replaces the stored one, keeping its judged metrics
/// unless the new upload carries its own.
/// </summary>
internal static partial class UploadRun
{
    /// <summary>The report schema this module reads (the harness's <c>RunReport.CurrentSchemaVersion</c>).</summary>
    public const int SupportedSchemaVersion = 1;

    /// <summary>Reports may only hold the sample corpus: questions and answers about it, never user data (spec 020).</summary>
    public const string SampleScope = "sample";

    public const long MaxBytes = 10 * 1024 * 1024;

    public sealed record Command(JsonElement Report, string UserSub) : IRequest<Result<Uploaded>>;

    /// <param name="Created">False when an existing run was replaced.</param>
    public sealed record Uploaded(RunSummary Run, bool Created);

    public sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.Report)
                .Must(r => r.ValueKind == JsonValueKind.Object).WithMessage("The report must be a JSON object.")
                .OverridePropertyName("report");
            When(c => c.Report.ValueKind == JsonValueKind.Object, () =>
            {
                RuleFor(c => Json.Int(c.Report, "schemaVersion"))
                    .Equal(SupportedSchemaVersion)
                    .WithMessage($"Only report schema version {SupportedSchemaVersion} is supported.")
                    .OverridePropertyName("schemaVersion");
                RuleFor(c => Json.String(c.Report, "runId"))
                    .NotEmpty().WithMessage("The report has no runId.")
                    .MaximumLength(EvaluationRunConfiguration.MaxIdLength)
                    .OverridePropertyName("runId");
                RuleFor(c => Json.Date(c.Report, "startedAt"))
                    .NotNull().WithMessage("The report has no startedAt.")
                    .OverridePropertyName("startedAt");
                RuleFor(c => Json.Object(c.Report, "configuration"))
                    .NotNull().WithMessage("The report has no configuration.")
                    .OverridePropertyName("configuration");
                RuleFor(c => Json.String(Json.Object(c.Report, "configuration"), "scope"))
                    .Equal(SampleScope)
                    .WithMessage($"Only runs over the '{SampleScope}' tag can be uploaded.")
                    .OverridePropertyName("configuration.scope");
                RuleFor(c => Json.String(Json.Object(c.Report, "configuration"), "datasetHash"))
                    .NotEmpty().MaximumLength(64).OverridePropertyName("configuration.datasetHash");
                RuleFor(c => Json.String(Json.Object(c.Report, "configuration"), "corpusHash"))
                    .NotEmpty().MaximumLength(64).OverridePropertyName("configuration.corpusHash");
                RuleFor(c => Json.Object(c.Report, "overall"))
                    .NotNull().WithMessage("The report has no overall metrics.")
                    .OverridePropertyName("overall");
                RuleFor(c => Json.Array(c.Report, "cases"))
                    .NotNull().WithMessage("The report has no cases.")
                    .OverridePropertyName("cases");
                RuleFor(c => Json.String(Json.Object(c.Report, "judge"), "judgeModel"))
                    .NotEmpty().When(c => Json.Object(c.Report, "judge") is not null)
                    .WithMessage("A judge result needs its judgeModel.")
                    .OverridePropertyName("judge.judgeModel");
            });
        }
    }

    internal sealed partial class Handler(EvaluationDbContext db, TimeProvider clock, ILogger<Handler> logger) : IRequestHandler<Command, Result<Uploaded>>
    {
        public async ValueTask<Result<Uploaded>> Handle(Command command, CancellationToken cancellationToken)
        {
            using var activity = EvaluationTelemetry.Source.StartActivity("evaluation.upload");
            var report = command.Report;
            var configuration = Json.Object(report, "configuration")!.Value;
            var judge = Json.Object(report, "judge");
            var id = Json.String(report, "runId")!;
            activity?.SetTag("run.id", id);

            var metrics = new Dictionary<string, object?>
            {
                ["valid"] = Json.Bool(report, "valid") ?? true,
                ["invalidReason"] = Json.String(report, "invalidReason"),
                ["answerMetricsMeasured"] = Json.Bool(report, "answerMetricsMeasured") ?? true,
                ["overall"] = Json.Object(report, "overall"),
                ["byCategory"] = Json.Object(report, "byCategory"),
                ["byQuestionLanguage"] = Json.Object(report, "byQuestionLanguage"),
                ["brokenCases"] = Json.Array(report, "brokenCases"),
                ["variants"] = Json.Array(report, "variants"),
            };

            var run = await db.Runs.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
            var created = run is null;
            run ??= db.Runs.Add(new EvaluationRun
            {
                Id = id,
                DatasetHash = "",
                CorpusHash = "",
                Configuration = "{}",
                Metrics = "{}",
                Cases = "[]",
                UploadedBy = command.UserSub,
            }).Entity;

            run.SchemaVersion = Json.Int(report, "schemaVersion")!.Value;
            run.StartedAt = Json.Date(report, "startedAt")!.Value;
            run.FinishedAt = Json.Date(report, "finishedAt");
            run.ChatModel = Json.String(configuration, "chatModel");
            run.EmbeddingModel = Json.String(configuration, "embeddingModel");
            // Reports from before spec 019 have no mode: their retrieval was vector only.
            run.RetrievalMode = Json.String(configuration, "retrievalMode") ?? "vector";
            run.Rerank = Json.Bool(configuration, "reranking") ?? false;
            run.DatasetHash = Json.String(configuration, "datasetHash")!;
            run.CorpusHash = Json.String(configuration, "corpusHash")!;
            run.GitCommit = Json.String(configuration, "gitCommit");
            run.Valid = Json.Bool(report, "valid") ?? true;
            run.CaseCount = Json.Array(report, "cases")!.Value.GetArrayLength();
            run.Configuration = configuration.GetRawText();
            run.Metrics = JsonSerializer.Serialize(metrics, Json.Options);
            run.Cases = Json.Array(report, "cases")!.Value.GetRawText();
            if (judge is { } judged)
            {
                // A judged upload replaces the run's judged metrics; one without them keeps what an earlier judge left (spec 020).
                run.JudgeModel = Json.String(judged, "judgeModel");
                run.Judge = judged.GetRawText();
            }

            run.UploadedBy = command.UserSub;
            run.UploadedAt = clock.GetUtcNow();

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException) when (created)
            {
                // Two uploads of a new run raced: the other one inserted it, so this one replaces it like any repeat upload.
                db.ChangeTracker.Clear();
                return await Handle(command, cancellationToken);
            }

            LogUploaded(logger, id, created, judge is not null);
            return new Uploaded(RunViews.Summary(run), created);
        }

        [LoggerMessage(Level = LogLevel.Information, Message = "Evaluation run {RunId} uploaded (new: {Created}, judged: {Judged})")]
        private static partial void LogUploaded(ILogger logger, string runId, bool created, bool judged);
    }
}
