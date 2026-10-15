using Microsoft.AspNetCore.Builder;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using RagAssistant.Modules.Documents.Common;
using RagAssistant.Modules.Documents.Options;
using RagAssistant.Modules.Documents.Storage;
using RagAssistant.Security;
using RagAssistant.SharedKernel.Common.Extensions;
using RagAssistant.SharedKernel.Common.Security;
using ModuleMediator = RagAssistant.Modules.Documents.Generated.Mediator;

namespace RagAssistant.Modules.Documents.Features.ImportDocument;

public sealed class ImportDocumentEndpoint : IEndpointGroup
{
    public void Map(RouteGroupBuilder api)
    {
        api.MapGroup("/documents")
            .MapPost(string.Empty, ImportAsync)
            .RequireAuthorization(PolicyNames.ContentOwner)
            .WithName("ImportDocument")
            .Produces<ImportDocument.Response>(StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> ImportAsync(
        HttpRequest request,
        ClaimsPrincipal user,
        ModuleMediator mediator,
        IOptions<ImportOptions> options,
        IOriginalFileStore store,
        DocumentsMetrics metrics,
        CancellationToken cancellationToken)
    {
        // Disposing discards a staged PDF the handler did not commit (rejected, duplicate, failed).
        await using var form = await UploadForm.ReadAsync(request, options.Value, store, cancellationToken);

        var command = new ImportDocument.Command(
            Title: form?.Field("title"),
            OwnerName: form?.Field("ownerName"),
            Language: form?.Field("language"),
            Tags: form?.Values("tags"),
            EffectiveDate: form?.Field("effectiveDate"),
            Source: form?.Field("source"),
            FileCount: form?.FileCount ?? 0,
            File: form?.File,
            UserId: user.GetSubject());

        var result = await mediator.Send(command, cancellationToken);
        if (result.IsFailure)
        {
            metrics.Rejected(result.Error.Code);
        }

        return result.ToHttpResult(r => TypedResults.Accepted($"/api/documents/{r.DocumentId}", r));
    }
}
