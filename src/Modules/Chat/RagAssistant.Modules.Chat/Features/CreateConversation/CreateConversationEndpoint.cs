using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RagAssistant.Modules.Chat.Common;
using RagAssistant.Security;
using RagAssistant.SharedKernel.Common.Extensions;
using RagAssistant.SharedKernel.Common.Security;
using ModuleMediator = RagAssistant.Modules.Chat.Generated.Mediator;

namespace RagAssistant.Modules.Chat.Features.CreateConversation;

public sealed class CreateConversationEndpoint : IEndpointGroup
{
    public void Map(RouteGroupBuilder api)
    {
        api.MapGroup("/chat")
            .MapPost("/conversations", async (ClaimsPrincipal user, ModuleMediator mediator, CancellationToken cancellationToken) =>
                (await mediator.Send(new CreateConversation.Command(user.GetSubject()), cancellationToken))
                    .ToHttpResult(c => TypedResults.Created($"/api/chat/conversations/{c.Id}", c)))
            .RequireAuthorization(PolicyNames.KnowledgeSeeker)
            .WithName("CreateConversation")
            .Produces<ConversationSummary>(StatusCodes.Status201Created);
    }
}
