using Microsoft.Extensions.AI;
using RagAssistant.Api.Api;
using RagAssistant.Api.Chat;
using RagAssistant.Modules.Chat;
using RagAssistant.Modules.Documents;
using RagAssistant.SharedKernel.Common.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddNpgsqlDataSource("ragassistant");

// The provider is wired here and nowhere else; modules only see IEmbeddingGenerator and IChatClient (ADR 0003).
builder.AddOllamaApiClient("embeddings").AddEmbeddingGenerator();

// Chat:Provider = Demo streams an extractive answer with no model download; retrieval stays real. The deadlines that count are
// the Chat module's (Chat:Generation); the ServiceDefaults attempt timeout only bounds the wait for the response headers.
var chat = builder.Configuration["Chat:Provider"] == "Demo"
    ? builder.Services.AddChatClient(services => new DemoChatClient(services.GetRequiredService<TimeProvider>()))
    : builder.AddOllamaApiClient("chat").AddChatClient();
chat.UseOpenTelemetry(sourceName: "RagAssistant.Chat.Model").UseLogging();

// In-memory only, no L2 (ADR 0008); the Documents module caches query embeddings in it.
builder.Services.AddHybridCache();
builder.Services.AddApiConventions();
builder.Services.AddApiAuthentication(builder.Environment);
builder.Services.AddEndpoints(typeof(Program).Assembly);
builder.Services.AddDocumentsModule(builder.Configuration);
builder.Services.AddChatModule(builder.Configuration);

var app = builder.Build();

app.UseApiConventions();
app.UseAuthentication();
app.UseAuthorization();

app.MapDefaultEndpoints();
app.MapEndpoints();
app.MapApiDocumentation();

await app.RunAsync();
