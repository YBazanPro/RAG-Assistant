using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using RagAssistant.Modules.Documents.Options;
using RagAssistant.Modules.Documents.Persistence;
using RagAssistant.Shared.Common.Extensions;
using RagAssistant.Shared.Common.Security;

namespace RagAssistant.Modules.Documents.Features.StreamIngestionEvents;

public sealed class StreamIngestionEventsEndpoint : IEndpointGroup
{
    public void Map(RouteGroupBuilder api)
    {
        api.MapGroup("/documents")
            .MapGet("/{documentId:guid}/ingestion/events", StreamAsync)
            .RequireAuthorization(PolicyNames.ContentOwner)
            .WithName("StreamIngestionEvents")
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream");
    }

    // Read straight from the DbContext: a stream is not a request/response pair, so it does not go through the Mediator.
    private static async Task<IResult> StreamAsync(
        Guid documentId,
        [FromHeader(Name = "Last-Event-ID")] string? lastEventId,
        DocumentsDbContext db,
        IOptions<IngestionOptions> options,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var target = await StreamIngestionEvents.ResolveAsync(db, documentId, cancellationToken);
        return target.ToHttpResult(t => TypedResults.ServerSentEvents(
            StreamIngestionEvents.ReadAsync(db, options.Value, clock, t, StreamIngestionEvents.ParseLastEventId(lastEventId), cancellationToken)));
    }
}
