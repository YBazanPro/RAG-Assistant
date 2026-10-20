using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RagAssistant.Security;
using RagAssistant.SharedKernel.Common.Extensions;
using RagAssistant.SharedKernel.Common.Security;
using ModuleMediator = RagAssistant.Modules.Chat.Generated.Mediator;

namespace RagAssistant.Modules.Chat.Features.ExportConversation;

public sealed class ExportConversationEndpoint : IEndpointGroup
{
    public const string ContentType = "text/markdown; charset=utf-8";

    public void Map(RouteGroupBuilder api)
    {
        api.MapGroup("/chat")
            .MapGet("/conversations/{conversationId:guid}/export", async (
                Guid conversationId,
                ClaimsPrincipal user,
                ModuleMediator mediator,
                CancellationToken cancellationToken) =>
                (await mediator.Send(new ExportConversation.Query(user.GetSubject(), conversationId), cancellationToken))
                    // A download (Content-Disposition: attachment), so a plain link through the BFF saves it.
                    .ToHttpResult(file => TypedResults.File(Encoding.UTF8.GetBytes(file.Markdown), ContentType, file.FileName)))
            .RequireAuthorization(PolicyNames.KnowledgeSeeker)
            .WithName("ExportConversation")
            .Produces<string>(StatusCodes.Status200OK, ContentType);
    }
}
