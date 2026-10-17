using RagAssistant.SharedKernel.Common.Security;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Net.Http.Headers;
using RagAssistant.Security;
using RagAssistant.SharedKernel.Common.Extensions;
using ModuleMediator = RagAssistant.Modules.Documents.Generated.Mediator;

namespace RagAssistant.Modules.Documents.Features.DownloadVersionFile;

public sealed class DownloadVersionFileEndpoint : IEndpointGroup
{
    public void Map(RouteGroupBuilder api)
    {
        api.MapGroup("/documents")
            .MapGet("/{documentId:guid}/versions/{number:int}/file", DownloadAsync)
            .RequireAuthorization(PolicyNames.KnowledgeSeeker)
            .WithName("DownloadDocumentVersionFile")
            .Produces(StatusCodes.Status200OK, contentType: DownloadVersionFile.PdfContentType)
            .Produces(StatusCodes.Status206PartialContent, contentType: DownloadVersionFile.PdfContentType);
    }

    private static async Task<IResult> DownloadAsync(
        Guid documentId,
        int number,
        HttpContext http,
        ClaimsPrincipal user,
        ModuleMediator mediator,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new DownloadVersionFile.Query(documentId, number, user.GetSubject()), cancellationToken);
        return result.ToHttpResult(file =>
        {
            var headers = http.Response.Headers;
            // The file is data, never a page of this application: no sniffing, no scripts, no caching of private content.
            headers.XContentTypeOptions = "nosniff";
            headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
            headers.CacheControl = "private, no-store";
            headers.ContentDisposition = new ContentDispositionHeaderValue(file.Inline ? "inline" : "attachment") { FileNameStar = file.FileName }.ToString();

            return file.Stream is not null
                ? TypedResults.File(file.Stream, file.ContentType, enableRangeProcessing: true)
                : TypedResults.File(file.Text!, file.ContentType, enableRangeProcessing: true);
        });
    }
}
