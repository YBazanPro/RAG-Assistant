using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RagAssistant.Modules.Documents.Common;
using RagAssistant.Shared.Common.Extensions;
using RagAssistant.Shared.Common.Security;
using ModuleMediator = RagAssistant.Modules.Documents.Generated.Mediator;

namespace RagAssistant.Modules.Documents.Features.RestoreDocument;

public sealed class RestoreDocumentEndpoint : IEndpointGroup
{
    public void Map(RouteGroupBuilder api)
    {
        api.MapGroup("/documents")
            .MapPost("/{documentId:guid}/restore", RestoreAsync)
            .RequireAuthorization(PolicyNames.ContentOwner)
            .WithName("RestoreDocument")
            .Produces<GetDocument.GetDocument.Response>();
    }

    private static async Task<IResult> RestoreAsync(
        Guid documentId,
        HttpContext http,
        ClaimsPrincipal user,
        ModuleMediator mediator,
        DocumentsMetrics metrics,
        CancellationToken cancellationToken) =>
        await LifecycleResponses.DocumentOrProblemAsync(
            await mediator.Send(new RestoreDocument.Command(documentId, user.GetSubject()), cancellationToken), http, mediator, metrics, cancellationToken);
}
