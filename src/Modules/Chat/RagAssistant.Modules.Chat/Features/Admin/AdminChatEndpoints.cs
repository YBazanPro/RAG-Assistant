using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RagAssistant.Security;
using RagAssistant.SharedKernel.Common;
using RagAssistant.SharedKernel.Common.Extensions;
using ModuleMediator = RagAssistant.Modules.Chat.Generated.Mediator;

namespace RagAssistant.Modules.Chat.Features.Admin;

/// <summary>The maintainers' read-only views of requests, feedback and configuration (spec 017).</summary>
public sealed class AdminChatEndpoints : IEndpointGroup
{
    public void Map(RouteGroupBuilder api)
    {
        var group = api.MapGroup("/admin/chat")
            .RequireAuthorization(PolicyNames.Maintainer);

        group.MapGet("/requests", async (
                string[]? status,
                string? feedback,
                string? page,
                string? pageSize,
                string? questionLanguage,
                ModuleMediator mediator,
                CancellationToken cancellationToken) =>
                (await mediator.Send(new ListRequests.Query(status, feedback, page, pageSize, questionLanguage), cancellationToken)).ToHttpResult())
            .WithName("AdminListRequests")
            .Produces<Page<RequestListItem>>();

        group.MapGet("/requests/{id:guid}", async (Guid id, ModuleMediator mediator, CancellationToken cancellationToken) =>
                (await mediator.Send(new GetRequest.Query(id), cancellationToken)).ToHttpResult())
            .WithName("AdminGetRequest")
            .Produces<RequestDetail>();

        group.MapGet("/summary", async (string? window, ModuleMediator mediator, CancellationToken cancellationToken) =>
                (await mediator.Send(new GetSummary.Query(window), cancellationToken)).ToHttpResult())
            .WithName("AdminChatSummary")
            .Produces<ChatSummary>();

        group.MapGet("/feedback", async (string? page, string? pageSize, ModuleMediator mediator, CancellationToken cancellationToken) =>
                (await mediator.Send(new ListFeedback.Query(page, pageSize), cancellationToken)).ToHttpResult())
            .WithName("AdminListFeedback")
            .Produces<Page<FeedbackListItem>>();

        group.MapGet("/configuration", async (ModuleMediator mediator, CancellationToken cancellationToken) =>
                (await mediator.Send(new GetChatConfiguration.Query(), cancellationToken)).ToHttpResult())
            .WithName("AdminChatConfiguration")
            .Produces<ChatConfiguration>();
    }
}
