using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RagAssistant.Modules.Documents.Common;
using RagAssistant.Security;
using RagAssistant.SharedKernel.Common.Extensions;
using RagAssistant.SharedKernel.Common.Security;
using ModuleMediator = RagAssistant.Modules.Documents.Generated.Mediator;

namespace RagAssistant.Modules.Documents.Features.ArchiveDocument;

public sealed class ArchiveDocumentEndpoint : IEndpointGroup
{
    public void Map(RouteGroupBuilder api)
    {
        api.MapGroup("/documents")
            .MapPost("/{documentId:guid}/archive", ArchiveAsync)
            .RequireAuthorization(PolicyNames.ContentOwner)
            .WithName("ArchiveDocument")
            .Produces<GetDocument.GetDocument.Response>();
    }

    private static async Task<IResult> ArchiveAsync(
        Guid documentId,
        HttpContext http,
        ClaimsPrincipal user,
        ModuleMediator mediator,
        DocumentsMetrics metrics,
        CancellationToken cancellationToken) =>
        await LifecycleResponses.DocumentOrProblemAsync(
            await mediator.Send(new ArchiveDocument.Command(documentId, user.GetSubject()), cancellationToken), http, mediator, metrics, cancellationToken);
}
