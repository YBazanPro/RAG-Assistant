using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Net.Http.Headers;
using RagAssistant.Modules.Documents.Common;
using RagAssistant.Security;
using RagAssistant.SharedKernel.Common.Extensions;
using RagAssistant.SharedKernel.Common.Security;
using ModuleMediator = RagAssistant.Modules.Documents.Generated.Mediator;

namespace RagAssistant.Modules.Documents.Features.UpdateDocument;

public sealed class UpdateDocumentEndpoint : IEndpointGroup
{
    public sealed record Body(string? Title, string? OwnerName, string? Language, string[]? Tags, string? EffectiveDate, string? Source, string? Visibility = null);

    public void Map(RouteGroupBuilder api)
    {
        api.MapGroup("/documents")
            .MapPut("/{documentId:guid}", UpdateAsync)
            .RequireAuthorization(PolicyNames.ContentOwner)
            .WithName("UpdateDocument")
            .Produces<GetDocument.GetDocument.Response>();
    }

    private static async Task<IResult> UpdateAsync(
        Guid documentId,
        Body body,
        HttpContext http,
        ClaimsPrincipal user,
        ModuleMediator mediator,
        DocumentsMetrics metrics,
        CancellationToken cancellationToken)
    {
        var expected = DocumentETag.TryParse(http.Request.Headers[HeaderNames.IfMatch].ToString(), out var revision) ? revision : (int?)null;
        var command = new UpdateDocument.Command(
            documentId, expected, body.Title, body.OwnerName, body.Language, body.Tags, body.EffectiveDate, body.Source, user.GetSubject(), body.Visibility);

        return await LifecycleResponses.DocumentOrProblemAsync(
            await mediator.Send(command, cancellationToken), user.GetSubject(), http, mediator, metrics, cancellationToken);
    }
}
