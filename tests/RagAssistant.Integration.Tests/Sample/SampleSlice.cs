using System.Collections.Concurrent;
using FluentValidation;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ModuleMediator = RagAssistant.Integration.Tests.Generated.Mediator;
using RagAssistant.Security;
using RagAssistant.SharedKernel.Common;
using RagAssistant.SharedKernel.Common.Behaviors;
using RagAssistant.SharedKernel.Common.Extensions;

namespace RagAssistant.Integration.Tests.Sample;

// Test-only stand-in for a module (spec 002): its own Mediator, validators and endpoint group.
public sealed record EchoCommand(string? Text) : IRequest<Result<EchoResponse>>;

public sealed record EchoResponse(string Text);

public sealed record Ok(bool Value);

public sealed class EchoValidator : AbstractValidator<EchoCommand>
{
    public EchoValidator()
    {
        RuleFor(c => c.Text).NotEmpty().MaximumLength(20);
    }
}

public sealed class EchoHandler : IRequestHandler<EchoCommand, Result<EchoResponse>>
{
    private static readonly ConcurrentBag<string> _handled = [];

    public static IReadOnlyCollection<string> Handled => _handled;

    public ValueTask<Result<EchoResponse>> Handle(EchoCommand request, CancellationToken cancellationToken)
    {
        _handled.Add(request.Text!);
        return new(Result.Success(new EchoResponse(request.Text!)));
    }
}

public sealed record FailCommand(string Kind, string? Message) : IRequest<Result<EchoResponse>>;

public sealed class FailHandler : IRequestHandler<FailCommand, Result<EchoResponse>>
{
    public async ValueTask<Result<EchoResponse>> Handle(FailCommand request, CancellationToken cancellationToken)
    {
        switch (request.Kind)
        {
            case "not-found":
                return Error.NotFound("sample.not_found", "The sample was not found.");
            case "conflict":
                return Error.Conflict("sample.conflict", "The sample already exists.");
            case "unavailable":
                return Error.Unavailable("sample.unavailable", "The sample dependency is unavailable.");
            case "throws":
                throw new InvalidOperationException(request.Message ?? "boom");
            case "slow":
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return Result.Success(new EchoResponse("never"));
            default:
                return Error.Failure("sample.failure", "Unknown kind.");
        }
    }
}

public sealed class SampleEndpoints : IEndpointGroup
{
    public void Map(RouteGroupBuilder api)
    {
        var group = api.MapGroup("/_test");

        group.MapPost("/echo", async (EchoCommand command, ModuleMediator mediator, CancellationToken ct) =>
            (await mediator.Send(command, ct)).ToHttpResult());

        group.MapGet("/not-found", Fail("not-found"));
        group.MapGet("/conflict", Fail("conflict"));
        group.MapGet("/unavailable", Fail("unavailable"));
        group.MapGet("/throws", async (string? message, ModuleMediator mediator, CancellationToken ct) =>
            (await mediator.Send(new FailCommand("throws", message), ct)).ToHttpResult());
        group.MapGet("/slow", Fail("slow"));

        group.MapGet("/anonymous", () => TypedResults.Ok(new Ok(true))).AllowAnonymous();
        group.MapGet("/authenticated", () => TypedResults.Ok(new Ok(true)));
        group.MapGet("/seeker", () => TypedResults.Ok(new Ok(true))).RequireAuthorization(PolicyNames.KnowledgeSeeker);
        group.MapGet("/owner", () => TypedResults.Ok(new Ok(true))).RequireAuthorization(PolicyNames.ContentOwner);
        group.MapGet("/maintainer", () => TypedResults.Ok(new Ok(true))).RequireAuthorization(PolicyNames.Maintainer);
    }

    private static Func<ModuleMediator, CancellationToken, Task<IResult>> Fail(string kind) =>
        async (mediator, ct) => (await mediator.Send(new FailCommand(kind, null), ct)).ToHttpResult();
}

public static class SampleSliceRegistration
{
    public static IServiceCollection AddSampleSlice(this IServiceCollection services)
    {
        services.AddMediator(options =>
        {
            options.Namespace = "RagAssistant.Integration.Tests.Generated";
            options.ServiceLifetime = ServiceLifetime.Scoped;
            options.Assemblies = [typeof(SampleSliceRegistration)];
            options.PipelineBehaviors = [typeof(ValidationBehavior<,>)];
        });
        services.AddValidatorsFromAssembly(typeof(SampleSliceRegistration).Assembly);
        services.AddEndpoints(typeof(SampleSliceRegistration).Assembly);
        return services;
    }
}
