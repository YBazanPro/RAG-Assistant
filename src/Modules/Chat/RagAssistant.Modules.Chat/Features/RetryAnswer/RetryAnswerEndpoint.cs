using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RagAssistant.Modules.Chat.Answering;
using RagAssistant.Security;
using RagAssistant.SharedKernel.Common.Extensions;
using RagAssistant.SharedKernel.Common.Security;
using ModuleMediator = RagAssistant.Modules.Chat.Generated.Mediator;

namespace RagAssistant.Modules.Chat.Features.RetryAnswer;

public sealed class RetryAnswerEndpoint : IEndpointGroup
{
    public void Map(RouteGroupBuilder api)
    {
        api.MapGroup("/chat")
            .MapPost("/conversations/{conversationId:guid}/messages/{assistantMessageId:guid}/retry", RetryAsync)
            .RequireAuthorization(PolicyNames.KnowledgeSeeker)
            .WithName("RetryAnswer")
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream");
    }

    private static async Task<IResult> RetryAsync(
        Guid conversationId,
        Guid assistantMessageId,
        ClaimsPrincipal user,
        ModuleMediator mediator,
        AnswerPipeline pipeline,
        CancellationToken cancellationToken)
    {
        var accepted = await mediator.Send(new AcceptRetry.Command(user.GetSubject(), conversationId, assistantMessageId), cancellationToken);
        return accepted.ToHttpResult(a => TypedResults.ServerSentEvents(pipeline.RunAsync(a, cancellationToken)));
    }
}
