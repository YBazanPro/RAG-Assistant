using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RagAssistant.Modules.Evaluation.Common;
using RagAssistant.Modules.Evaluation.Persistence;
using RagAssistant.SharedKernel.Common;

namespace RagAssistant.Modules.Evaluation.Features.Runs;

internal static class ListRuns
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    /// <param name="ChatModel">Only runs of this chat model, matched exactly; null lists every run.</param>
    public sealed record Query(string? Page, string? PageSize, string? ChatModel) : IRequest<Result<Page<RunSummary>>>;

    public sealed class Validator : AbstractValidator<Query>
    {
        public Validator() => this.AddPagingRules(q => q.Page, q => q.PageSize, MaxPageSize);
    }

    internal sealed class Handler(EvaluationDbContext db) : IRequestHandler<Query, Result<Page<RunSummary>>>
    {
        public async ValueTask<Result<Page<RunSummary>>> Handle(Query query, CancellationToken cancellationToken)
        {
            var page = PagingRules.ParsePage(query.Page);
            var pageSize = PagingRules.ParsePageSize(query.PageSize, DefaultPageSize);

            var runs = db.Runs.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(query.ChatModel))
            {
                runs = runs.Where(r => r.ChatModel == query.ChatModel);
            }

            var total = await runs.CountAsync(cancellationToken);
            // The case lists stay in the database: a row only needs the headline numbers out of the metrics.
            var rows = await runs
                .OrderByDescending(r => r.StartedAt)
                .ThenBy(r => r.Id)
                .Skip(PagingRules.Skip(page, pageSize))
                .Take(pageSize)
                .Select(r => new Domain.EvaluationRun
                {
                    Id = r.Id,
                    StartedAt = r.StartedAt,
                    ChatModel = r.ChatModel,
                    EmbeddingModel = r.EmbeddingModel,
                    JudgeModel = r.JudgeModel,
                    RetrievalMode = r.RetrievalMode,
                    Rerank = r.Rerank,
                    DatasetHash = r.DatasetHash,
                    CorpusHash = r.CorpusHash,
                    GitCommit = r.GitCommit,
                    Valid = r.Valid,
                    UploadedAt = r.UploadedAt,
                    Configuration = "{}",
                    Metrics = r.Metrics,
                    Judge = r.Judge,
                    Cases = "[]",
                    UploadedBy = r.UploadedBy,
                })
                .ToListAsync(cancellationToken);

            return new Page<RunSummary>([.. rows.Select(RunViews.Summary)], page, pageSize, total);
        }
    }
}

internal static class GetRun
{
    public sealed record Query(string Id) : IRequest<Result<RunDetail>>;

    internal sealed class Handler(EvaluationDbContext db) : IRequestHandler<Query, Result<RunDetail>>
    {
        public async ValueTask<Result<RunDetail>> Handle(Query query, CancellationToken cancellationToken)
        {
            var run = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == query.Id, cancellationToken);
            return run is null ? EvaluationErrors.RunNotFound : RunViews.Detail(run);
        }
    }
}

/// <summary>A maintainer removes a run for good (spec 020); there is no archive.</summary>
internal static partial class DeleteRun
{
    public sealed record Command(string Id, string UserSub) : IRequest<Result<bool>>;

    internal sealed partial class Handler(EvaluationDbContext db, ILogger<Handler> logger) : IRequestHandler<Command, Result<bool>>
    {
        public async ValueTask<Result<bool>> Handle(Command command, CancellationToken cancellationToken)
        {
            var deleted = await db.Runs.Where(r => r.Id == command.Id).ExecuteDeleteAsync(cancellationToken);
            if (deleted == 0)
            {
                return EvaluationErrors.RunNotFound;
            }

            LogDeleted(logger, command.Id, command.UserSub);
            return true;
        }

        [LoggerMessage(Level = LogLevel.Information, Message = "Evaluation run {RunId} deleted by {UserSub}")]
        private static partial void LogDeleted(ILogger logger, string runId, string userSub);
    }
}
