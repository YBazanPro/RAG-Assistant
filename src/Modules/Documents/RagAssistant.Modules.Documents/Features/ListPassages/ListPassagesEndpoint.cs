using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RagAssistant.Shared.Common.Extensions;
using RagAssistant.Shared.Common.Security;
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
                ModuleMediator mediator,
                CancellationToken cancellationToken) =>
                (await mediator.Send(new ListPassages.Query(documentId, number, page, pageSize, focus), cancellationToken)).ToHttpResult())
            .RequireAuthorization(PolicyNames.KnowledgeSeeker)
            .WithName("ListDocumentPassages")
            .Produces<ListPassages.Response>();
    }
}
