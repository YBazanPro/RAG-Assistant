using System.Text.Json;
using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using RagAssistant.Modules.Evaluation.Common;
using RagAssistant.Modules.Evaluation.Persistence;
using RagAssistant.SharedKernel.Common;

namespace RagAssistant.Modules.Evaluation.Features.Runs;

/// <summary>
/// Two runs side by side (spec 020), like the harness <c>compare</c>: every metric of every group with B − A, and the configuration
/// keys that differ, which explain the deltas. Runs over different datasets can be compared; the difference is flagged.
/// </summary>
internal static class CompareRuns
{
    public sealed record Query(string? A, string? B) : IRequest<Result<Comparison>>;

    public sealed record ConfigurationDifference(string Key, JsonElement? A, JsonElement? B);

    public sealed record MetricDelta(string Name, double? A, double? B, double? Delta);

    /// <param name="Name"><c>overall</c>, <c>category:&lt;name&gt;</c>, <c>language:&lt;code&gt;</c> or <c>judge</c>.</param>
    public sealed record MetricGroup(string Name, IReadOnlyList<MetricDelta> Metrics);

    public sealed record Comparison(
        RunSummary A,
        RunSummary B,
        bool DatasetDiffers,
        bool CorpusDiffers,
        IReadOnlyList<ConfigurationDifference> ConfigurationDifferences,
        IReadOnlyList<MetricGroup> Groups);

    public sealed class Validator : AbstractValidator<Query>
    {
        public Validator()
        {
            RuleFor(q => q.A).NotEmpty().WithMessage("Name run A.").OverridePropertyName("a");
            RuleFor(q => q.B).NotEmpty().WithMessage("Name run B.").OverridePropertyName("b");
        }
    }

    internal sealed class Handler(EvaluationDbContext db) : IRequestHandler<Query, Result<Comparison>>
    {
        public async ValueTask<Result<Comparison>> Handle(Query query, CancellationToken cancellationToken)
        {
            var runs = await db.Runs.AsNoTracking().Where(r => r.Id == query.A || r.Id == query.B).ToListAsync(cancellationToken);
            var a = runs.FirstOrDefault(r => r.Id == query.A);
            var b = runs.FirstOrDefault(r => r.Id == query.B);
            if (a is null || b is null)
            {
                return EvaluationErrors.RunNotFound;
            }

            return Compare(RunViews.Detail(a), RunViews.Detail(b));
        }
    }

    public static Comparison Compare(RunDetail a, RunDetail b)
    {
        var groups = new List<MetricGroup> { Group("overall", Json.Object(a.Metrics, "overall"), Json.Object(b.Metrics, "overall")) };
        foreach (var (section, prefix) in new[] { ("byCategory", "category"), ("byQuestionLanguage", "language") })
        {
            var sa = Json.Object(a.Metrics, section);
            var sb = Json.Object(b.Metrics, section);
            foreach (var key in Keys(sa, sb))
            {
                groups.Add(Group($"{prefix}:{key}", Json.Object(sa, key), Json.Object(sb, key)));
            }
        }

        if (a.Judge is not null || b.Judge is not null)
        {
            groups.Add(Group("judge", Json.Object(a.Judge, "overall"), Json.Object(b.Judge, "overall")));
        }

        return new Comparison(
            a.Run,
            b.Run,
            a.Run.DatasetHash != b.Run.DatasetHash,
            a.Run.CorpusHash != b.Run.CorpusHash,
            [.. Keys(a.Configuration, b.Configuration)
                .Select(k => new ConfigurationDifference(k, Json.Property(a.Configuration, k), Json.Property(b.Configuration, k)))
                .Where(d => d.A?.GetRawText() != d.B?.GetRawText())],
            groups);
    }

    private static MetricGroup Group(string name, JsonElement? a, JsonElement? b) =>
        new(name, [.. Keys(a, b)
            .Where(k => Json.Number(a, k) is not null || Json.Number(b, k) is not null)
            .Select(k =>
            {
                var va = Json.Number(a, k);
                var vb = Json.Number(b, k);
                return new MetricDelta(k, va, vb, va is { } x && vb is { } y ? Math.Round(y - x, 4) : null);
            })]);

    /// <summary>The property names of both objects, A's order first, then what only B has.</summary>
    private static List<string> Keys(JsonElement? a, JsonElement? b)
    {
        static IEnumerable<string> Names(JsonElement? e) =>
            e is { ValueKind: JsonValueKind.Object } o ? o.EnumerateObject().Select(p => p.Name) : [];

        return [.. Names(a).Concat(Names(b)).Distinct(StringComparer.Ordinal)];
    }
}
