using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RagAssistant.Modules.Chat.Common;
using RagAssistant.Security;
using RagAssistant.SharedKernel.Common.Extensions;
using RagAssistant.SharedKernel.Common.Security;
using ModuleMediator = RagAssistant.Modules.Chat.Generated.Mediator;

namespace RagAssistant.Modules.Chat.Features.SetScope;

public sealed class SetScopeEndpoint : IEndpointGroup
{
    public sealed record ScopeRequest(Guid[]? DocumentIds, string[]? Tags);

    public void Map(RouteGroupBuilder api)
    {
        api.MapGroup("/chat")
            .MapPut("/conversations/{conversationId:guid}/scope", async (
                Guid conversationId,
                ScopeRequest? request,
                ClaimsPrincipal user,
                ModuleMediator mediator,
                CancellationToken cancellationToken) =>
                (await mediator.Send(new SetScope.Command(user.GetSubject(), conversationId, request?.DocumentIds, request?.Tags), cancellationToken))
                    .ToHttpResult())
            .RequireAuthorization(PolicyNames.KnowledgeSeeker)
            .WithName("SetConversationScope")
            .Produces<ConversationScope>();
    }
}
