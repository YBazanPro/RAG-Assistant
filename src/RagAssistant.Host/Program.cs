using RagAssistant.Host.Api;
using RagAssistant.Shared.Common.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddNpgsqlDataSource("ragassistant");

builder.Services.AddApiConventions();
builder.Services.AddEndpoints(typeof(Program).Assembly);

var app = builder.Build();

app.UseApiConventions();

app.MapDefaultEndpoints();
app.MapEndpoints();
app.MapApiDocumentation();

await app.RunAsync();
