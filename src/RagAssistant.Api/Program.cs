using Microsoft.Extensions.AI;
using RagAssistant.Api.Api;
using RagAssistant.Api.Chat;
using RagAssistant.Api.Retrieval;
using RagAssistant.Modules.Chat;
using RagAssistant.Modules.Documents;
using RagAssistant.Modules.Evaluation;
using RagAssistant.SharedKernel.Common.Extensions;
using RagAssistant.SharedKernel.Contracts.Retrieval;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddNpgsqlDataSource("ragassistant");

// The provider is wired here and nowhere else; modules only see IEmbeddingGenerator and IChatClient (ADR 0003).
builder.AddOllamaApiClient("embeddings").AddEmbeddingGenerator();

// Chat:Provider = Demo streams an extractive answer with no model download; retrieval stays real. The deadlines that count are
// the Chat module's (Chat:Generation); the ServiceDefaults attempt timeout only bounds the wait for the response headers.
// Chat:Provider = OpenAICompatible is the opt-in hosted provider (spec 018): questions and passages leave the machine.
var chat = builder.Configuration["Chat:Provider"] switch
{
    "Demo" => builder.Services.AddChatClient(services => new DemoChatClient(services.GetRequiredService<TimeProvider>())),
    OpenAICompatibleChat.ProviderName => builder.Services.AddOpenAICompatibleChat(builder.Configuration),
    _ => builder.AddOllamaApiClient("chat").AddChatClient()
        .Use(inner => new OllamaRequestOptions(
            inner,
            builder.Configuration.GetValue<bool>(OllamaRequestOptions.ThinkConfigurationKey),
            builder.Configuration.GetValue(OllamaRequestOptions.ContextWindowConfigurationKey, OllamaRequestOptions.DefaultContextWindowTokens))),
};
chat.UseOpenTelemetry(sourceName: "RagAssistant.Chat.Model").UseLogging();
if (builder.Configuration["Chat:Provider"] == "Demo")
{
    // The demo client only extracts passages, so it has no questions to suggest (spec 020): every version is "skipped".
    builder.Configuration["Documents:Suggestions:Enabled"] = "false";
}

// The reranker exists only when the AppHost started one (launch profile https-rerank, spec 019, ADR 0010). Without it the Documents
// module's fallback makes every rerank "skipped", so a search never fails for it.
if (builder.Configuration[$"services:{TeiPassageReranker.ResourceName}:http:0"] is not null)
{
    var rerankModel = builder.Configuration[TeiPassageReranker.ModelConfigurationKey] ?? TeiPassageReranker.DefaultModel;
    builder.Services.AddHttpClient<IPassageReranker, TeiPassageReranker>((http, _) => new TeiPassageReranker(http, rerankModel))
        .ConfigureHttpClient(client => client.BaseAddress = new Uri($"http://{TeiPassageReranker.ResourceName}/"));
}

// In-memory only, no L2 (ADR 0008); the Documents module caches query embeddings in it.
builder.Services.AddHybridCache();
builder.Services.AddApiConventions();
builder.Services.AddApiAuthentication(builder.Environment);
builder.Services.AddEndpoints(typeof(Program).Assembly);
builder.Services.AddDocumentsModule(builder.Configuration);
builder.Services.AddChatModule(builder.Configuration);
builder.Services.AddEvaluationModule(builder.Configuration);

var app = builder.Build();

app.UseApiConventions();
app.UseAuthentication();
app.UseAuthorization();

app.MapDefaultEndpoints();
app.MapEndpoints();
app.MapApiDocumentation();

await app.RunAsync();
