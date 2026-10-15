using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RagAssistant.Modules.Documents.Common;
using RagAssistant.Security;
using RagAssistant.SharedKernel.Common.Extensions;
using RagAssistant.SharedKernel.Common.Security;
using ModuleMediator = RagAssistant.Modules.Documents.Generated.Mediator;

namespace RagAssistant.Modules.Documents.Features.ReprocessDocument;

public sealed class ReprocessDocumentEndpoint : IEndpointGroup
{
    public void Map(RouteGroupBuilder api)
    {
        api.MapGroup("/documents")
            .MapPost("/{documentId:guid}/reprocess", ReprocessAsync)
            .RequireAuthorization(PolicyNames.ContentOwner)
            .WithName("ReprocessDocument")
            .Produces<NewVersionResponse>(StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> ReprocessAsync(
        Guid documentId, ClaimsPrincipal user, ModuleMediator mediator, DocumentsMetrics metrics, CancellationToken cancellationToken) =>
        LifecycleResponses.Rejected(
            await mediator.Send(new ReprocessDocument.Command(documentId, user.GetSubject()), cancellationToken),
            metrics,
            r => TypedResults.Accepted($"/api/documents/{r.DocumentId}", r));
}
