using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RagAssistant.Security;
using RagAssistant.SharedKernel.Common.Extensions;
using RagAssistant.SharedKernel.Common.Security;
using ModuleMediator = RagAssistant.Modules.Chat.Generated.Mediator;

namespace RagAssistant.Modules.Chat.Features.Feedback;

public sealed class FeedbackEndpoints : IEndpointGroup
{
    public sealed record RatingRequest(string? Rating);

    public sealed record ReportRequest(string? Category, string? Comment);

    public void Map(RouteGroupBuilder api)
    {
        var group = api.MapGroup("/chat/conversations/{conversationId:guid}/messages/{messageId:guid}");

        group.MapPut("/rating", async (
                Guid conversationId, Guid messageId, RatingRequest? request, ClaimsPrincipal user, ModuleMediator mediator, CancellationToken cancellationToken) =>
                (await mediator.Send(new SetRating.Command(user.GetSubject(), conversationId, messageId, request?.Rating), cancellationToken)).ToHttpResult())
            .RequireAuthorization(PolicyNames.KnowledgeSeeker)
            .WithName("SetRating")
            .Produces<SetRating.RatingView>();

        group.MapDelete("/rating", async (
                Guid conversationId, Guid messageId, ClaimsPrincipal user, ModuleMediator mediator, CancellationToken cancellationToken) =>
                (await mediator.Send(new ClearRating.Command(user.GetSubject(), conversationId, messageId), cancellationToken))
                    .ToHttpResult(() => TypedResults.NoContent()))
            .RequireAuthorization(PolicyNames.KnowledgeSeeker)
            .WithName("ClearRating")
            .Produces(StatusCodes.Status204NoContent);

        group.MapPost("/reports", async (
                Guid conversationId, Guid messageId, ReportRequest? request, ClaimsPrincipal user, ModuleMediator mediator, CancellationToken cancellationToken) =>
                (await mediator.Send(new ReportIssue.Command(user.GetSubject(), conversationId, messageId, request?.Category, request?.Comment), cancellationToken))
                    .ToHttpResult(r => TypedResults.Created($"/api/chat/conversations/{conversationId}/messages/{messageId}/reports/{r.Id}", r)))
            .RequireAuthorization(PolicyNames.KnowledgeSeeker)
            .WithName("ReportIssue")
            .Produces<ReportIssue.ReportView>(StatusCodes.Status201Created);
    }
}
