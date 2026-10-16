using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.FluentUI.AspNetCore.Components;
using RagAssistant.Security;
using RagAssistant.Web.Client.Chat;
using RagAssistant.Web.Client.Documents;

namespace RagAssistant.Web.Client.Security;

/// <summary>Services both the server (prerendering) and the WebAssembly client register, so a page renders the same in both.</summary>
public static class UiServiceExtensions
{
    public static IServiceCollection AddUiServices(this IServiceCollection services)
    {
        services.AddFluentUIComponents();
        services.AddCascadingAuthenticationState();
        services.AddUiAuthorization();
        services.TryAddSingleton(TimeProvider.System);

        // The HttpClient exists only in the browser; it is resolved on first use, which never happens during prerendering.
        services.AddScoped<IDocumentsApi>(sp => new DocumentsApi(sp.GetRequiredService<HttpClient>));
        services.AddScoped<IIngestionEventSource>(sp => new IngestionEventSource(sp.GetRequiredService<HttpClient>));
        services.AddScoped<IFileUploader, BrowserFileUploader>();
        services.AddScoped<IChatApi>(sp => new ChatApi(sp.GetRequiredService<HttpClient>));
        services.AddScoped<BrowserChatInterop>();
        services.AddScoped<IClipboard>(sp => sp.GetRequiredService<BrowserChatInterop>());
        services.AddScoped<IThreadScroller>(sp => sp.GetRequiredService<BrowserChatInterop>());
        services.AddScoped<IAnswerEventSource>(sp => new AnswerEventSource(sp.GetRequiredService<HttpClient>));
        return services;
    }

    // Same policy names and role mapping as the API; the UI only uses them to shape pages and navigation.
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
