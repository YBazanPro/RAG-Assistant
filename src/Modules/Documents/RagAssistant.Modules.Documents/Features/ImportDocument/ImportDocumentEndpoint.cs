using Microsoft.AspNetCore.Builder;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
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
    // Room for the metadata fields and multipart framing on top of the largest allowed file.
    private const int FormOverheadBytes = 64 * 1024;

    public void Map(RouteGroupBuilder api)
    {
        // The form is read by hand rather than bound: the body-size limit must be set before the body is touched,
        // and binding would read it first. CSRF is covered by the BFF's X-Requested-With check; the Host only takes bearer tokens.
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
        var limit = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false })
        {
            limit.MaxRequestBodySize = options.Value.MaxFileSizeBytes + FormOverheadBytes;
        }

        var form = await ReadFormAsync(request, cancellationToken);
        var file = form?.Files.Count == 1 ? form.Files[0] : null;

        var command = new ImportDocument.Command(
            Title: Field(form, "title"),
            OwnerName: Field(form, "ownerName"),
            Language: Field(form, "language"),
            Tags: form?["tags"].Select(t => t ?? string.Empty).ToArray(),
            EffectiveDate: Field(form, "effectiveDate"),
            Source: Field(form, "source"),
            FileCount: form?.Files.Count ?? 0,
            File: file is null ? null : new ImportDocument.UploadedFile(file.FileName, file.ContentType, file.Length, file.OpenReadStream()),
            UserId: user.GetSubject());

        var result = await mediator.Send(command, cancellationToken);
        if (result.IsFailure)
        {
            metrics.Rejected(result.Error.Code);
        }

        return result.ToHttpResult(r => TypedResults.Accepted($"/api/documents/{r.DocumentId}", r));
    }

    private static async Task<IFormCollection?> ReadFormAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
        {
            return null;
        }

        try
        {
            return await request.ReadFormAsync(cancellationToken);
        }
        catch (InvalidDataException)
        {
            // A malformed multipart body is reported like a missing file.
            return null;
        }
    }

    private static string? Field(IFormCollection? form, string name) =>
        form is not null && form.TryGetValue(name, out var values) && values.Count > 0 ? values[0] : null;
}
