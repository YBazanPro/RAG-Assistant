using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RagAssistant.Modules.Chat.Common;
using RagAssistant.Security;
using RagAssistant.SharedKernel.Common;
using RagAssistant.SharedKernel.Common.Extensions;
using RagAssistant.SharedKernel.Common.Security;
using ModuleMediator = RagAssistant.Modules.Chat.Generated.Mediator;

namespace RagAssistant.Modules.Chat.Features.ListConversations;

public sealed class ListConversationsEndpoint : IEndpointGroup
{
    public void Map(RouteGroupBuilder api)
    {
        api.MapGroup("/chat")
            .MapGet("/conversations", async (
                string? page,
                string? pageSize,
                ClaimsPrincipal user,
                ModuleMediator mediator,
                CancellationToken cancellationToken) =>
                (await mediator.Send(new ListConversations.Query(user.GetSubject(), page, pageSize), cancellationToken)).ToHttpResult())
            .RequireAuthorization(PolicyNames.KnowledgeSeeker)
            .WithName("ListConversations")
            .Produces<Page<ConversationSummary>>();
    }
}
