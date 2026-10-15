using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using RagAssistant.Modules.Documents.Features.GetDocument;
using RagAssistant.SharedKernel.Common;
using RagAssistant.SharedKernel.Common.Extensions;
using ModuleMediator = RagAssistant.Modules.Documents.Generated.Mediator;

namespace RagAssistant.Modules.Documents.Common;

/// <summary>Shared tail of the lifecycle endpoints: count a rejection, or answer with the document as it is now.</summary>
internal static class LifecycleResponses
{
    public static async Task<IResult> DocumentOrProblemAsync(
        Result<Guid> result, HttpContext http, ModuleMediator mediator, DocumentsMetrics metrics, CancellationToken cancellationToken)
    {
        if (result.IsFailure)
        {
            metrics.LifecycleRejected(result.Error.Code);
            return result.ToHttpResult();
        }

        var document = await mediator.Send(new GetDocument.Query(result.Value), cancellationToken);
        if (document.IsSuccess)
        {
            http.Response.Headers[HeaderNames.ETag] = DocumentETag.Format(document.Value.Revision);
        }

        return document.ToHttpResult();
    }

    public static IResult Rejected<T>(Result<T> result, DocumentsMetrics metrics, Func<T, IResult> onSuccess)
    {
        if (result.IsFailure)
        {
            metrics.LifecycleRejected(result.Error.Code);
        }

        return result.ToHttpResult(onSuccess);
    }
}
