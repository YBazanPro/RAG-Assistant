using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using RagAssistant.Modules.Documents.Common;
using RagAssistant.Modules.Documents.Options;
using RagAssistant.Shared.Common.Extensions;
using RagAssistant.Shared.Common.Security;
using ModuleMediator = RagAssistant.Modules.Documents.Generated.Mediator;

namespace RagAssistant.Modules.Documents.Features.UploadVersion;

public sealed class UploadVersionEndpoint : IEndpointGroup
{
    public void Map(RouteGroupBuilder api)
    {
        api.MapGroup("/documents")
            .MapPost("/{documentId:guid}/versions", UploadAsync)
            .DisableAntiforgery() // multipart: CSRF is covered by the BFF's X-Requested-With check, like import
            .RequireAuthorization(PolicyNames.ContentOwner)
            .WithName("UploadVersion")
            .Produces<NewVersionResponse>(StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> UploadAsync(
        Guid documentId,
        HttpRequest request,
        ClaimsPrincipal user,
        ModuleMediator mediator,
        IOptions<ImportOptions> options,
        DocumentsMetrics metrics,
        CancellationToken cancellationToken)
    {
        var form = await UploadForm.ReadAsync(request, options.Value.MaxFileSizeBytes, cancellationToken);
        var command = new UploadVersion.Command(documentId, form?.Files.Count ?? 0, UploadForm.SingleFile(form), user.GetSubject());

        return LifecycleResponses.Rejected(
            await mediator.Send(command, cancellationToken), metrics, r => TypedResults.Accepted($"/api/documents/{r.DocumentId}", r));
    }
}
