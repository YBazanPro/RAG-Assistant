using RagAssistant.Web.Bff;
using RagAssistant.Web.Client.Security;
using RagAssistant.Web.Components;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
    context.ProblemDetails.Extensions["traceId"] = BffAuthExtensions.TraceId(context.HttpContext));

builder.Services.AddBffAuthentication(builder.Environment);
builder.Services.AddBffAuthorization();
builder.Services.AddBffProxy(builder.Configuration);

builder.Services.AddUiServices();
builder.Services.AddRazorComponents()
    .AddInteractiveWebAssemblyComponents()
    .AddAuthenticationStateSerialization(options => options.SerializeAllClaims = true);

var app = builder.Build();

// Baseline headers for every response, including redirects and errors. The CSP only forbids framing (clickjacking);
// a full script policy needs care with the WebAssembly runtime and is left for later.
app.Use((context, next) =>
{
    var headers = context.Response.Headers;
    headers.XContentTypeOptions = "nosniff";
    headers["Referrer-Policy"] = "same-origin";
    headers.ContentSecurityPolicy = "frame-ancestors 'none'";
    return next();
});

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/status/500", createScopeForErrors: true);
    app.UseHsts();
}

// Pages get the 403/404 views with their real status code; API responses keep their own bodies.
app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/api"),
    branch => branch.UseStatusCodePagesWithReExecute("/status/{0}", createScopeForStatusCodePages: true));
app.UseHttpsRedirection();
// Explicit, and after the status code pages, so a re-executed request (/status/403) is routed again.
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapDefaultEndpoints();
app.MapBffEndpoints();
app.MapBffProxy();
app.MapRazorComponents<App>()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(RagAssistant.Web.Client.Security.UiServiceExtensions).Assembly);

await app.RunAsync();

public partial class Program;
