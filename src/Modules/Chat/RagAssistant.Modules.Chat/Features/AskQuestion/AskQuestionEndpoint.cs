using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RagAssistant.Modules.Chat.Answering;
using RagAssistant.Security;
using RagAssistant.SharedKernel.Common.Extensions;
using RagAssistant.SharedKernel.Common.Security;
using ModuleMediator = RagAssistant.Modules.Chat.Generated.Mediator;

namespace RagAssistant.Modules.Chat.Features.AskQuestion;

public sealed class AskQuestionEndpoint : IEndpointGroup
{
    public sealed record AskRequest(string? Question, string? Language, string[]? Tags);

    public void Map(RouteGroupBuilder api)
    {
        api.MapGroup("/chat")
            .MapPost("/conversations/{conversationId:guid}/messages", AskAsync)
            .RequireAuthorization(PolicyNames.KnowledgeSeeker)
            .WithName("AskQuestion")
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream");
    }

    // Everything that can fail with a status code is decided by the command; once it succeeds the response is a stream.
    private static async Task<IResult> AskAsync(
        Guid conversationId,
        AskRequest? request,
        ClaimsPrincipal user,
        ModuleMediator mediator,
        AnswerPipeline pipeline,
        CancellationToken cancellationToken)
    {
        var command = new AcceptQuestion.Command(user.GetSubject(), conversationId, request?.Question, request?.Language, request?.Tags);
        var accepted = await mediator.Send(command, cancellationToken);
        return accepted.ToHttpResult(a => TypedResults.ServerSentEvents(pipeline.RunAsync(a, cancellationToken)));
    }
}
