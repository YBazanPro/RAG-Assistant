using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using RagAssistant.Modules.Evaluation.Common;
using RagAssistant.Security;
using RagAssistant.SharedKernel.Common;
using RagAssistant.SharedKernel.Common.Extensions;
using RagAssistant.SharedKernel.Common.Security;
using ModuleMediator = RagAssistant.Modules.Evaluation.Generated.Mediator;

namespace RagAssistant.Modules.Evaluation.Features.Runs;

/// <summary>Evaluation runs for maintainers (spec 020, roadmap 5.3). The harness uploads; the admin area reads, compares and deletes.</summary>
public sealed class EvaluationEndpoints : IEndpointGroup
{
    public void Map(RouteGroupBuilder api)
    {
        var group = api.MapGroup("/admin/evaluation")
            .RequireAuthorization(PolicyNames.Maintainer);

        group.MapPost("/runs", UploadAsync)
            .WithName("UploadEvaluationRun")
            .Produces<RunSummary>(StatusCodes.Status201Created)
            .Produces<RunSummary>()
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge);

        group.MapGet("/runs", async (string? page, string? pageSize, string? chatModel, ModuleMediator mediator, CancellationToken cancellationToken) =>
                (await mediator.Send(new ListRuns.Query(page, pageSize, chatModel), cancellationToken)).ToHttpResult())
            .WithName("ListEvaluationRuns")
            .Produces<Page<RunSummary>>();

        group.MapGet("/runs/{id}", async (string id, ModuleMediator mediator, CancellationToken cancellationToken) =>
                (await mediator.Send(new GetRun.Query(id), cancellationToken)).ToHttpResult())
            .WithName("GetEvaluationRun")
            .Produces<RunDetail>();

        group.MapGet("/compare", async (string? a, string? b, ModuleMediator mediator, CancellationToken cancellationToken) =>
                (await mediator.Send(new CompareRuns.Query(a, b), cancellationToken)).ToHttpResult())
            .WithName("CompareEvaluationRuns")
            .Produces<CompareRuns.Comparison>();

        group.MapDelete("/runs/{id}", async (string id, ClaimsPrincipal user, ModuleMediator mediator, CancellationToken cancellationToken) =>
                (await mediator.Send(new DeleteRun.Command(id, user.GetSubject()), cancellationToken)).ToHttpResult(_ => TypedResults.NoContent()))
            .WithName("DeleteEvaluationRun")
            .Produces(StatusCodes.Status204NoContent);
    }

    /// <summary>The body is read here, not bound, so the 10 MiB limit and malformed JSON both answer before the command runs.</summary>
    private static async Task<IResult> UploadAsync(HttpRequest request, ClaimsPrincipal user, ModuleMediator mediator, CancellationToken cancellationToken)
    {
        if (request.ContentLength > UploadRun.MaxBytes)
        {
            return TooLarge();
        }

        if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = UploadRun.MaxBytes;
        }

        JsonElement report;
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
            report = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Result.Failure<UploadRun.Uploaded>(
                    Error.Validation(Result.ValidationFailedCode, "One or more validation errors occurred."),
                    new Dictionary<string, string[]> { ["report"] = ["The body is not valid JSON."] })
                .ToHttpResult();
        }

        var result = await mediator.Send(new UploadRun.Command(report, user.GetSubject()), cancellationToken);
        return result.ToHttpResult(uploaded => uploaded.Created
            ? TypedResults.Created($"/api/admin/evaluation/runs/{Uri.EscapeDataString(uploaded.Run.Id)}", uploaded.Run)
            : TypedResults.Ok(uploaded.Run));
    }

    private static ProblemHttpResult TooLarge() => TypedResults.Problem(
        statusCode: StatusCodes.Status413PayloadTooLarge,
        detail: $"A report is at most {UploadRun.MaxBytes / (1024 * 1024)} MiB.",
        extensions: new Dictionary<string, object?> { ["code"] = "request.too_large" });
}
