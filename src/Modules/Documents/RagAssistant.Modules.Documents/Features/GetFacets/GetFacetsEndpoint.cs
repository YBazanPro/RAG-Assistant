using RagAssistant.SharedKernel.Common.Security;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RagAssistant.Security;
using RagAssistant.SharedKernel.Common.Extensions;
using ModuleMediator = RagAssistant.Modules.Documents.Generated.Mediator;

namespace RagAssistant.Modules.Documents.Features.GetFacets;

public sealed class GetFacetsEndpoint : IEndpointGroup
{
    public void Map(RouteGroupBuilder api)
    {
        api.MapGroup("/documents")
            .MapGet("/facets", async (ClaimsPrincipal user, ModuleMediator mediator, CancellationToken cancellationToken) =>
                (await mediator.Send(new GetFacets.Query(user.GetSubject()), cancellationToken)).ToHttpResult())
            .RequireAuthorization(PolicyNames.KnowledgeSeeker)
            .WithName("GetDocumentFacets")
            .Produces<GetFacets.Response>();
    }
}
