using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using RagAssistant.Shared.Common.Security;

namespace RagAssistant.Web.Client.Security;

/// <summary>Services both the server (prerendering) and the WebAssembly client register, so a page renders the same in both.</summary>
public static class UiServiceExtensions
{
    public static IServiceCollection AddUiServices(this IServiceCollection services)
    {
        services.AddFluentUIComponents();
        services.AddCascadingAuthenticationState();
        services.AddUiAuthorization();
        return services;
    }

    // Same policy names and role mapping as the Host; the UI only uses them to shape pages and navigation.
    public static IServiceCollection AddUiAuthorization(this IServiceCollection services)
    {
        services.AddAuthorizationCore(options =>
        {
            options.AddPolicy(PolicyNames.KnowledgeSeeker, policy => policy.RequireRole(RoleNames.KnowledgeSeeker));
            options.AddPolicy(PolicyNames.ContentOwner, policy => policy.RequireRole(RoleNames.ContentOwner));
            options.AddPolicy(PolicyNames.Maintainer, policy => policy.RequireRole(RoleNames.Maintainer));
        });

        return services;
    }
}
