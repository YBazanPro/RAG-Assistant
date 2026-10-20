using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RagAssistant.Modules.Chat.Common;
using RagAssistant.Security;
using RagAssistant.SharedKernel.Common.Extensions;
using RagAssistant.SharedKernel.Common.Security;
using ModuleMediator = RagAssistant.Modules.Chat.Generated.Mediator;

namespace RagAssistant.Modules.Chat.Features.RenameConversation;

public sealed class RenameConversationEndpoint : IEndpointGroup
{
    public sealed record Body(string? Title);

    public void Map(RouteGroupBuilder api)
    {
        api.MapGroup("/chat")
            .MapPatch("/conversations/{conversationId:guid}", async (
                Guid conversationId,
                Body body,
                ClaimsPrincipal user,
                ModuleMediator mediator,
                CancellationToken cancellationToken) =>
                (await mediator.Send(new RenameConversation.Command(user.GetSubject(), conversationId, body.Title), cancellationToken)).ToHttpResult())
            .RequireAuthorization(PolicyNames.KnowledgeSeeker)
            .WithName("RenameConversation")
            .Produces<ConversationSummary>();
    }
}
