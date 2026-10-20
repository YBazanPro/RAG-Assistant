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

namespace RagAssistant.Modules.Documents.Features.EditSuggestions;

public sealed class EditSuggestionsEndpoint : IEndpointGroup
{
    public sealed record Body(string?[]? Questions);

    public void Map(RouteGroupBuilder api)
    {
        api.MapGroup("/documents")
            .MapPut("/{documentId:guid}/suggestions", EditAsync)
            .RequireAuthorization(PolicyNames.ContentOwner)
            .WithName("EditSuggestions")
            .Produces<GetDocument.GetDocument.Response>();
    }

    private static async Task<IResult> EditAsync(
        Guid documentId,
        Body body,
        HttpContext http,
        ClaimsPrincipal user,
        ModuleMediator mediator,
        DocumentsMetrics metrics,
        CancellationToken cancellationToken)
    {
        var expected = DocumentETag.TryParse(http.Request.Headers[HeaderNames.IfMatch].ToString(), out var revision) ? revision : (int?)null;
        var command = new EditSuggestions.Command(documentId, expected, body.Questions, user.GetSubject());
        return await LifecycleResponses.DocumentOrProblemAsync(
            await mediator.Send(command, cancellationToken), user.GetSubject(), http, mediator, metrics, cancellationToken);
    }
}
