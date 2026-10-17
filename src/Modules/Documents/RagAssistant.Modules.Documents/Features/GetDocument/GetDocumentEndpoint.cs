using RagAssistant.SharedKernel.Common.Security;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Net.Http.Headers;
using RagAssistant.Modules.Documents.Common;
using RagAssistant.Security;
using RagAssistant.SharedKernel.Common.Extensions;
using ModuleMediator = RagAssistant.Modules.Documents.Generated.Mediator;

namespace RagAssistant.Modules.Documents.Features.GetDocument;

public sealed class GetDocumentEndpoint : IEndpointGroup
{
    public void Map(RouteGroupBuilder api)
    {
        api.MapGroup("/documents")
            .MapGet("/{documentId:guid}", async (Guid documentId, HttpContext http, ClaimsPrincipal user, ModuleMediator mediator, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(new GetDocument.Query(documentId, user.GetSubject()), cancellationToken);
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
