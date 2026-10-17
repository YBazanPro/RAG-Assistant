using RagAssistant.SharedKernel.Common.Security;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RagAssistant.Security;
using RagAssistant.SharedKernel.Common.Extensions;
using ModuleMediator = RagAssistant.Modules.Documents.Generated.Mediator;

namespace RagAssistant.Modules.Documents.Features.ListPassages;

public sealed class ListPassagesEndpoint : IEndpointGroup
{
    public void Map(RouteGroupBuilder api)
    {
        api.MapGroup("/documents")
            .MapGet("/{documentId:guid}/versions/{number:int}/passages", async (
                Guid documentId,
                int number,
                string? page,
                string? pageSize,
                string? focus,
                ClaimsPrincipal user,
                ModuleMediator mediator,
                CancellationToken cancellationToken) =>
                (await mediator.Send(new ListPassages.Query(documentId, number, page, pageSize, focus, user.GetSubject()), cancellationToken)).ToHttpResult())
            .RequireAuthorization(PolicyNames.KnowledgeSeeker)
            .WithName("ListDocumentPassages")
            .Produces<ListPassages.Response>();
    }
}
