using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RagAssistant.Security;
using RagAssistant.SharedKernel.Common;
using RagAssistant.SharedKernel.Common.Extensions;
using ModuleMediator = RagAssistant.Modules.Documents.Generated.Mediator;

namespace RagAssistant.Modules.Documents.Features.Admin;

/// <summary>The maintainers' read-only views of corpus health, failures, private documents and configuration (spec 017).</summary>
public sealed class AdminDocumentsEndpoints : IEndpointGroup
{
    public void Map(RouteGroupBuilder api)
    {
        var group = api.MapGroup("/admin/documents")
            .RequireAuthorization(PolicyNames.Maintainer);

        group.MapGet("/health", async (ModuleMediator mediator, CancellationToken cancellationToken) =>
                (await mediator.Send(new GetHealth.Query(), cancellationToken)).ToHttpResult())
            .WithName("AdminDocumentsHealth")
            .Produces<DocumentsHealth>();

        group.MapGet("/failures", async (string? page, string? pageSize, ModuleMediator mediator, CancellationToken cancellationToken) =>
                (await mediator.Send(new ListFailures.Query(page, pageSize), cancellationToken)).ToHttpResult())
            .WithName("AdminListIngestionFailures")
            .Produces<Page<FailureItem>>();

        group.MapGet("/private", async (string? page, string? pageSize, ModuleMediator mediator, CancellationToken cancellationToken) =>
                (await mediator.Send(new ListPrivateDocuments.Query(page, pageSize), cancellationToken)).ToHttpResult())
            .WithName("AdminListPrivateDocuments")
            .Produces<Page<PrivateDocumentItem>>();

        group.MapGet("/configuration", async (ModuleMediator mediator, CancellationToken cancellationToken) =>
                (await mediator.Send(new GetDocumentsConfiguration.Query(), cancellationToken)).ToHttpResult())
            .WithName("AdminDocumentsConfiguration")
            .Produces<DocumentsConfiguration>();
    }
}
