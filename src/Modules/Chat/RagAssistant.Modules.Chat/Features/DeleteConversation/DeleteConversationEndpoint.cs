using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RagAssistant.Security;
using RagAssistant.SharedKernel.Common.Extensions;
using RagAssistant.SharedKernel.Common.Security;
using ModuleMediator = RagAssistant.Modules.Chat.Generated.Mediator;

namespace RagAssistant.Modules.Chat.Features.DeleteConversation;

public sealed class DeleteConversationEndpoint : IEndpointGroup
{
    public void Map(RouteGroupBuilder api)
    {
        api.MapGroup("/chat")
            .MapDelete("/conversations/{conversationId:guid}", async (
                Guid conversationId,
                ClaimsPrincipal user,
                ModuleMediator mediator,
                CancellationToken cancellationToken) =>
                (await mediator.Send(new DeleteConversation.Command(user.GetSubject(), conversationId), cancellationToken))
                    .ToHttpResult(() => TypedResults.NoContent()))
            .RequireAuthorization(PolicyNames.KnowledgeSeeker)
            .WithName("DeleteConversation")
            .Produces(StatusCodes.Status204NoContent);
    }
}
