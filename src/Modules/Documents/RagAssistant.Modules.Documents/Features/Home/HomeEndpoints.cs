using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RagAssistant.Security;
using RagAssistant.SharedKernel.Common.Extensions;
using RagAssistant.SharedKernel.Common.Security;
using ModuleMediator = RagAssistant.Modules.Documents.Generated.Mediator;

namespace RagAssistant.Modules.Documents.Features.Home;

/// <summary>The two reads behind the home page (spec 020); the page calls them directly, with no contract in between.</summary>
public sealed class HomeEndpoints : IEndpointGroup
{
    public void Map(RouteGroupBuilder api)
    {
        var documents = api.MapGroup("/documents");

        documents.MapGet("/suggestions", async (string? count, ClaimsPrincipal user, ModuleMediator mediator, CancellationToken cancellationToken) =>
                (await mediator.Send(new GetSuggestions.Query(count, user.GetSubject()), cancellationToken)).ToHttpResult())
            .RequireAuthorization(PolicyNames.KnowledgeSeeker)
            .WithName("GetSuggestedQuestions")
            .Produces<IReadOnlyList<GetSuggestions.Suggestion>>();

        documents.MapGet("/summary", async (ClaimsPrincipal user, ModuleMediator mediator, CancellationToken cancellationToken) =>
                (await mediator.Send(new GetCorpusSummary.Query(user.GetSubject()), cancellationToken)).ToHttpResult())
            .RequireAuthorization(PolicyNames.KnowledgeSeeker)
            .WithName("GetCorpusSummary")
            .Produces<GetCorpusSummary.Response>();
    }
}
