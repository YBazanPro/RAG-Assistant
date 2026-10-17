using RagAssistant.SharedKernel.Common.Security;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RagAssistant.Security;
using RagAssistant.SharedKernel.Common.Extensions;
using ModuleMediator = RagAssistant.Modules.Documents.Generated.Mediator;

namespace RagAssistant.Modules.Documents.Features.RetryIngestion;

public sealed class RetryIngestionEndpoint : IEndpointGroup
{
    public void Map(RouteGroupBuilder api)
    {
        api.MapGroup("/documents")
            .MapPost("/{documentId:guid}/ingestion/retry", async (Guid documentId, ClaimsPrincipal user, ModuleMediator mediator, CancellationToken cancellationToken) =>
                (await mediator.Send(new RetryIngestion.Command(documentId, user.GetSubject()), cancellationToken))
                    .ToHttpResult(r => TypedResults.Accepted($"/api/documents/{r.DocumentId}", r)))
            .RequireAuthorization(PolicyNames.ContentOwner)
            .WithName("RetryIngestion")
            .Produces<RetryIngestion.Response>(StatusCodes.Status202Accepted);
    }
}
