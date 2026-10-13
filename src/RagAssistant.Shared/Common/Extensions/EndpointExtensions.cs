using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace RagAssistant.Shared.Common.Extensions;

/// <summary>A feature's endpoints. Implementations need a public parameterless constructor and receive the "/api" group.</summary>
public interface IEndpointGroup
{
    void Map(RouteGroupBuilder api);
}

public static class EndpointExtensions
{
    public const string ApiPrefix = "/api";

    public static IServiceCollection AddEndpoints(this IServiceCollection services, Assembly assembly)
    {
        services.AddSingleton(new EndpointAssembly(assembly));
        return services;
    }

    public static IEndpointRouteBuilder MapEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup(ApiPrefix);

        var groups = app.ServiceProvider.GetServices<EndpointAssembly>()
            .Select(a => a.Assembly)
            .Distinct()
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsAbstract: false, IsInterface: false } && t.IsAssignableTo(typeof(IEndpointGroup)));

        foreach (var type in groups)
        {
            ((IEndpointGroup)Activator.CreateInstance(type)!).Map(api);
        }

        return app;
    }

    private sealed record EndpointAssembly(Assembly Assembly);
}
