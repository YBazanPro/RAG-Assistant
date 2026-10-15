using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RagAssistant.Security;
using RagAssistant.SharedKernel.Common;
using RagAssistant.SharedKernel.Common.Extensions;
using ModuleMediator = RagAssistant.Modules.Documents.Generated.Mediator;

namespace RagAssistant.Modules.Documents.Features.ListDocuments;

public sealed class ListDocumentsEndpoint : IEndpointGroup
{
    public void Map(RouteGroupBuilder api)
    {
        api.MapGroup("/documents")
            .MapGet(string.Empty, async (
                string? q,
                string? status,
                string? language,
                string? owner,
                string[]? tags,
                string? effectiveFrom,
                string? effectiveTo,
                string? sort,
                string? page,
                string? pageSize,
                ModuleMediator mediator,
                CancellationToken cancellationToken) =>
                (await mediator.Send(
                    new ListDocuments.Query(q, status, language, owner, tags ?? [], effectiveFrom, effectiveTo, sort, page, pageSize),
                    cancellationToken)).ToHttpResult())
            .RequireAuthorization(PolicyNames.KnowledgeSeeker)
            .WithName("ListDocuments")
            .Produces<Page<ListDocuments.DocumentSummary>>();
    }
}
