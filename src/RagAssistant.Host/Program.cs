using RagAssistant.Host.Api;
using RagAssistant.Modules.Documents;
using RagAssistant.Shared.Common.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddNpgsqlDataSource("ragassistant");

// The provider is wired here and nowhere else; modules only see IEmbeddingGenerator (ADR 0003).
builder.AddOllamaApiClient("embeddings").AddEmbeddingGenerator();

builder.Services.AddApiConventions();
builder.Services.AddApiAuthentication(builder.Environment);
builder.Services.AddEndpoints(typeof(Program).Assembly);
builder.Services.AddDocumentsModule(builder.Configuration);

var app = builder.Build();

app.UseApiConventions();
app.UseAuthentication();
app.UseAuthorization();

app.MapDefaultEndpoints();
app.MapEndpoints();
app.MapApiDocumentation();

await app.RunAsync();
