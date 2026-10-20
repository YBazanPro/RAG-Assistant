using System.Text.Json;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using RagAssistant.Security;
using RagAssistant.Web.Client.Admin;
using RagAssistant.Web.Client.Documents;
using RagAssistant.Web.Tests.Chat;

namespace RagAssistant.Web.Tests.Admin;

public sealed class FakeEvaluationApi : IEvaluationApi
{
    public List<EvaluationRunSummary> Runs { get; } = [];

    public List<string> DeleteCalls { get; } = [];

    public Func<string, ApiResult<EvaluationRunDetail>> OnGet { get; set; } = id => ApiResult.Ok(EvaluationTabTests.Detail(id));

    public Task<ApiResult<PagedResult<EvaluationRunSummary>>> ListRunsAsync(int page, CancellationToken cancellationToken) =>
        Task.FromResult(ApiResult.Ok(new PagedResult<EvaluationRunSummary>([.. Runs], page, 20, Runs.Count)));

    public Task<ApiResult<EvaluationRunDetail>> GetRunAsync(string id, CancellationToken cancellationToken) => Task.FromResult(OnGet(id));

    public Task<ApiResult<EvaluationComparison>> CompareAsync(string a, string b, CancellationToken cancellationToken) =>
        Task.FromResult(ApiResult.Ok(new EvaluationComparison(
            Runs.Single(r => r.Id == a), Runs.Single(r => r.Id == b), true, false,
            [new ConfigurationDifference("chatModel", Json("\"a\""), Json("\"b\""))],
            [new MetricGroup("overall", [new MetricDelta("recallAt5", 0.6, 0.85, 0.25)])])));

    public Task<ApiResult<bool>> DeleteRunAsync(string id, CancellationToken cancellationToken)
    {
        DeleteCalls.Add(id);
        Runs.RemoveAll(r => r.Id == id);
        return Task.FromResult(ApiResult.Ok(true));
    }

    public static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
}

// Spec 020, AC-24 and AC-25: the admin Evaluation tab.
public sealed class EvaluationTabTests
{
    public static EvaluationRunSummary Run(string id, double recall = 0.8) => new(
        id, new DateTimeOffset(2026, 10, 20, 2, 0, 0, TimeSpan.Zero), "qwen3:1.7b", "bge-m3", null, "hybrid", false, "d1d1d1d1d1", "c1c1c1c1c1", "abc", true,
        DateTimeOffset.UnixEpoch, new EvaluationHeadline(2, 0, recall, 0.7, 0.9, 0.75, 1, null));

    public static EvaluationRunDetail Detail(string id) => new(
        Run(id),
        FakeEvaluationApi.Json("""{"chatModel":"qwen3:1.7b","topK":8}"""),
        FakeEvaluationApi.Json("""
            {"overall":{"cases":2,"recallAt5":0.8},
             "byCategory":{"policy":{"cases":2,"recallAt5":0.8}},
             "byQuestionLanguage":{"fr":{"cases":1,"recallAt5":0.5}},
             "brokenCases":[]}
            """),
        null,
        FakeEvaluationApi.Json("""[{"id":"p-en-01","category":"policy","questionLanguage":"en","expectedBehaviour":"answer","status":"complete","metrics":{"recallAt5":1,"behaviourCorrect":true}}]"""));

    private static (BunitContext Ctx, FakeEvaluationApi Api) NewContext()
    {
        var ctx = ChatTestData.NewContext(out _, out _);
        var api = new FakeEvaluationApi();
        ctx.Services.AddSingleton<IEvaluationApi>(api);
        ctx.AddAuthorization().SetAuthorized("maintainer").SetPolicies([PolicyNames.Maintainer]);
        return (ctx, api);
    }

    // AC-25
    [Test]
    public async Task Tab_listsRunsWithHeadlineMetrics_andARunShowsCategoryAndLanguageMetrics()
    {
        var (ctx, api) = NewContext();
        await using var _ = ctx;
        api.Runs.Add(Run("run-1", recall: 0.83));

        var cut = ctx.Render<EvaluationTab>();
        cut.WaitForState(() => cut.Markup.Contains("qwen3:1.7b"), TimeSpan.FromSeconds(5));

        await Assert.That(cut.Markup).Contains("0.83");
        await Assert.That(cut.Markup).Contains("hybrid");

        cut.Find($"#{EvaluationTab.RowId("run-1")}").Click();
        cut.WaitForState(() => cut.Markup.Contains("Metrics per category"), TimeSpan.FromSeconds(5));

        await Assert.That(cut.Markup).Contains("policy");
        await Assert.That(cut.Markup).Contains("Metrics per question language");
        await Assert.That(cut.Markup).Contains("p-en-01");
        await Assert.That(cut.Markup).Contains("✓ correct");
    }

    // AC-24
    [Test]
    public async Task Delete_asksForConfirmationFirst()
    {
        var (ctx, api) = NewContext();
        await using var _ = ctx;
        api.Runs.Add(Run("run-1"));

        var cut = ctx.Render<EvaluationTab>();
        cut.WaitForState(() => cut.Markup.Contains("qwen3:1.7b"), TimeSpan.FromSeconds(5));
        cut.FindAll("fluent-button").Single(b => b.GetAttribute("aria-label") == "Delete run run-1").Click();

        await Assert.That(api.DeleteCalls.Count).IsEqualTo(0);
        await Assert.That(cut.Markup).Contains("Delete run run-1?");

        cut.FindAll("fluent-button").Single(b => b.TextContent.Trim() == "Delete").Click();
        cut.WaitForState(() => cut.Markup.Contains("Run run-1 deleted."), TimeSpan.FromSeconds(5));

        await Assert.That(api.DeleteCalls.Single()).IsEqualTo("run-1");
    }

    [Test]
    public async Task Compare_twoSelectedRuns_showsDeltasAndFlagsTheDataset()
    {
        var (ctx, api) = NewContext();
        await using var _ = ctx;
        api.Runs.AddRange([Run("run-a"), Run("run-b")]);

        var cut = ctx.Render<EvaluationTab>();
        cut.WaitForState(() => cut.Markup.Contains("run-b"), TimeSpan.FromSeconds(5));
        foreach (var box in cut.FindComponents<FluentCheckbox>())
        {
            await cut.InvokeAsync(() => box.Instance.CheckStateChanged.InvokeAsync(true));
        }

        cut.FindAll("fluent-button").Single(b => b.TextContent.Trim() == "Compare the 2 selected runs").Click();
        cut.WaitForState(() => cut.Markup.Contains("B − A"), TimeSpan.FromSeconds(5));

        await Assert.That(cut.Markup).Contains("+0.25");
        await Assert.That(cut.Markup).Contains("different golden datasets");
        await Assert.That(cut.Markup).Contains("⚠ chatModel");
    }
}
