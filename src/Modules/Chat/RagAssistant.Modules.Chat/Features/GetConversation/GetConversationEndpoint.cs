using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RagAssistant.Security;
using RagAssistant.SharedKernel.Common.Extensions;
using RagAssistant.SharedKernel.Common.Security;
using ModuleMediator = RagAssistant.Modules.Chat.Generated.Mediator;

namespace RagAssistant.Modules.Chat.Features.GetConversation;

public sealed class GetConversationEndpoint : IEndpointGroup
{
    public void Map(RouteGroupBuilder api)
    {
        api.MapGroup("/chat")
            .MapGet("/conversations/{conversationId:guid}", async (
                Guid conversationId,
                ClaimsPrincipal user,
                ModuleMediator mediator,
                CancellationToken cancellationToken) =>
                (await mediator.Send(new GetConversation.Query(user.GetSubject(), conversationId), cancellationToken)).ToHttpResult())
            .RequireAuthorization(PolicyNames.KnowledgeSeeker)
            .WithName("GetConversation")
            .Produces<GetConversation.ConversationDetail>();
    }
}
