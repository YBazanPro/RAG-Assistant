using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RagAssistant.Shared.Common.Extensions;
using RagAssistant.Shared.Common.Security;
using ModuleMediator = RagAssistant.Modules.Documents.Generated.Mediator;

namespace RagAssistant.Modules.Documents.Features.GetDocument;

public sealed class GetDocumentEndpoint : IEndpointGroup
{
    public void Map(RouteGroupBuilder api)
    {
        api.MapGroup("/documents")
            .MapGet("/{documentId:guid}", async (Guid documentId, ModuleMediator mediator, CancellationToken cancellationToken) =>
                (await mediator.Send(new GetDocument.Query(documentId), cancellationToken)).ToHttpResult())
            .RequireAuthorization(PolicyNames.KnowledgeSeeker)
            .WithName("GetDocument")
            .Produces<GetDocument.Response>();
    }
}
