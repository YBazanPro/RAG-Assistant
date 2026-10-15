using Microsoft.AspNetCore.Builder;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using RagAssistant.Modules.Documents.Common;
using RagAssistant.Modules.Documents.Options;
using RagAssistant.Shared.Common.Extensions;
using RagAssistant.Shared.Common.Security;
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
        DocumentsMetrics metrics,
        CancellationToken cancellationToken)
    {
        var form = await UploadForm.ReadAsync(request, options.Value.MaxFileSizeBytes, cancellationToken);

        var command = new ImportDocument.Command(
            Title: UploadForm.Field(form, "title"),
            OwnerName: UploadForm.Field(form, "ownerName"),
            Language: UploadForm.Field(form, "language"),
            Tags: form?["tags"].Select(t => t ?? string.Empty).ToArray(),
            EffectiveDate: UploadForm.Field(form, "effectiveDate"),
            Source: UploadForm.Field(form, "source"),
            FileCount: form?.Files.Count ?? 0,
            File: UploadForm.SingleFile(form),
            UserId: user.GetSubject());

        var result = await mediator.Send(command, cancellationToken);
        if (result.IsFailure)
        {
            metrics.Rejected(result.Error.Code);
        }

        return result.ToHttpResult(r => TypedResults.Accepted($"/api/documents/{r.DocumentId}", r));
    }
}
