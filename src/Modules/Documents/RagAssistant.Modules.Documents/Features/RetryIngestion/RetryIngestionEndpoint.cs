using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RagAssistant.Shared.Common.Extensions;
using RagAssistant.Shared.Common.Security;
using ModuleMediator = RagAssistant.Modules.Documents.Generated.Mediator;

namespace RagAssistant.Modules.Documents.Features.RetryIngestion;

public sealed class RetryIngestionEndpoint : IEndpointGroup
{
    public void Map(RouteGroupBuilder api)
    {
        api.MapGroup("/documents")
            .MapPost("/{documentId:guid}/ingestion/retry", async (Guid documentId, ModuleMediator mediator, CancellationToken cancellationToken) =>
                (await mediator.Send(new RetryIngestion.Command(documentId), cancellationToken))
                    .ToHttpResult(r => TypedResults.Accepted($"/api/documents/{r.DocumentId}", r)))
            .RequireAuthorization(PolicyNames.ContentOwner)
            .WithName("RetryIngestion")
            .Produces<RetryIngestion.Response>(StatusCodes.Status202Accepted);
    }
}
