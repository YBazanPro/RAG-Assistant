using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using RagAssistant.Web.Client.Security;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.AddUiServices();
builder.Services.AddAuthenticationStateDeserialization();

// The browser only ever calls its own origin; the BFF forwards /api/** to the API.
builder.Services.AddTransient<RequestedWithHandler>();
builder.Services.AddHttpClient("web", client => client.BaseAddress = new Uri(builder.HostEnvironment.BaseAddress))
    .AddHttpMessageHandler<RequestedWithHandler>();
builder.Services.AddScoped(services => services.GetRequiredService<IHttpClientFactory>().CreateClient("web"));

await builder.Build().RunAsync();
