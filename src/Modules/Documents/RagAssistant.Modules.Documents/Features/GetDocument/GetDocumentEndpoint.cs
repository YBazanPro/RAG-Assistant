using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Net.Http.Headers;
using RagAssistant.Modules.Documents.Common;
using RagAssistant.Shared.Common.Extensions;
using RagAssistant.Shared.Common.Security;
using ModuleMediator = RagAssistant.Modules.Documents.Generated.Mediator;

namespace RagAssistant.Modules.Documents.Features.GetDocument;

public sealed class GetDocumentEndpoint : IEndpointGroup
{
    public void Map(RouteGroupBuilder api)
    {
        api.MapGroup("/documents")
            .MapGet("/{documentId:guid}", async (Guid documentId, HttpContext http, ModuleMediator mediator, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(new GetDocument.Query(documentId), cancellationToken);
                if (result.IsSuccess)
                {
                    http.Response.Headers[HeaderNames.ETag] = DocumentETag.Format(result.Value.Revision);
                }

                return result.ToHttpResult();
            })
            .RequireAuthorization(PolicyNames.KnowledgeSeeker)
            .WithName("GetDocument")
            .Produces<GetDocument.Response>();
    }
}
