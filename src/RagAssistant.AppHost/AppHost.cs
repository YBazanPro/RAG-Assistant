// Keep in sync with PostgresFixture.ImageTag in tests/RagAssistant.Integration.Tests.
const string PostgresImageTag = "0.8.7-pg18-trixie";
// Keep in sync with KeycloakFixture.ImageTag in tests/RagAssistant.Integration.Tests.
const string KeycloakImageTag = "26.8";
const string OllamaImageTag = "0.40.4";
// The 1024 dimensions must match Documents:Embeddings:Dimensions and the passages.embedding column. The model name recorded on
// each version comes from the provider; Documents:Embeddings:Model (default "bge-m3") is only the fallback, so keep them equal.
const string EmbeddingModel = "bge-m3";
// The chat model chosen by spec 018's comparison on a CPU-only machine (ADR 0003 addendum). Another Ollama model is tried with the chat-model
// parameter, e.g. dotnet run --project src/RagAssistant.AppHost -- --Parameters:chat-model=qwen3.5:9b, or the user secret
// Parameters:chat-model; the AppHost pulls it, and the evaluation harness reads it back from the request traces.
const string DefaultChatModel = "ministral-3:8b";
// Dev-only secret of the ragassistant-web client; keep in sync with ragassistant-realm.json.
const string WebClientSecret = "ragassistant-web-dev-secret";

var builder = DistributedApplication.CreateBuilder(args);

// PostgreSQL 18 images expect the volume at /var/lib/postgresql (PGDATA lives in a versioned
// subdirectory), so the volume is mounted explicitly rather than via WithDataVolume.
var postgres = builder.AddPostgres("postgres")
    .WithImage("pgvector/pgvector")
    .WithImageTag(PostgresImageTag)
    .WithHostPort(5433)
    .WithVolume("ragassistant-postgres-data", "/var/lib/postgresql")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithPgWeb(pgweb => pgweb.WithLifetime(ContainerLifetime.Persistent));

var database = postgres.AddDatabase("ragassistant");

// No data volume and the default session lifetime: the container re-imports the committed realm file on every run.
var keycloak = builder.AddKeycloak("keycloak", port: 8180)
    .WithImageTag(KeycloakImageTag)
    .WithRealmImport("./Realms");

// Models live on a named volume, so the download happens once. The API does not wait for the model: ingestion
// pauses until it answers (spec 007), which keeps a fresh clone usable while bge-m3 is still being pulled.
var ollama = builder.AddOllama("ollama")
    .WithImageTag(OllamaImageTag)
    .WithDataVolume("ragassistant-ollama-models")
    // Ollama unloads an idle model after 5 minutes by default, and loading bge-m3 again made the first question after a pause
    // spend 5 to 12 seconds in retrieval. Keep the models in memory.
    .WithEnvironment("OLLAMA_KEEP_ALIVE", "24h")
    .WithLifetime(ContainerLifetime.Persistent);
var embeddings = ollama.AddModel("embeddings", EmbeddingModel);

// Chat:Provider = Demo (launch profile "https-demo") runs the API's deterministic demo chat client: no chat model is pulled.
var demoChat = builder.Configuration["Chat:Provider"] == "Demo";
// chat-provider = openai-compatible sends chat calls to a hosted OpenAI-compatible endpoint (OpenRouter unless openai-endpoint says
// otherwise) with the secret parameter openai-api-key, and chat-model is then the provider's model id (spec 018, ADR 0003).
// Questions and retrieved passages leave the machine; embeddings stay on the local bge-m3.
var hostedChat = builder.Configuration["Parameters:chat-provider"] == "openai-compatible";
var configuredModel = builder.Configuration["Parameters:chat-model"];
if (hostedChat && string.IsNullOrEmpty(configuredModel))
{
    throw new InvalidOperationException("chat-provider=openai-compatible needs chat-model, the provider's model id.");
}

var chatModel = configuredModel is { Length: > 0 } ? configuredModel : DefaultChatModel;
var chat = demoChat || hostedChat ? null : ollama.AddModel("chat", chatModel);

var migrations = builder.AddProject<Projects.RagAssistant_MigrationService>("migrations")
    .WithReference(database)
    .WaitFor(database);

// Original files of imported PDFs live outside the database, in a git-ignored folder at the repository root (spec 010).
// Passing the absolute path makes it visible in the dashboard; back it up together with the database.
var documentStore = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", ".data", "documents"));
Directory.CreateDirectory(documentStore);

var api = builder.AddProject<Projects.RagAssistant_Api>("api")
    .WithReference(database)
    .WithReference(keycloak)
    .WithReference(embeddings)
    .WithEnvironment("Documents__Storage__Path", documentStore)
    .WaitFor(database)
    .WaitFor(keycloak)
    .WaitForCompletion(migrations);
// Spec 021: every ApiEnvironment:<NAME> setting reaches the API as the environment variable <NAME>, so a run can change API options
// without a parameter per option. The nightly evaluation raises the chat deadlines for a CPU runner, e.g.
// --ApiEnvironment:Chat__Generation__FirstTokenTimeout=00:02:00 on the command line.
foreach (var setting in builder.Configuration.GetSection("ApiEnvironment").GetChildren())
{
    if (setting.Value is { } value)
    {
        api.WithEnvironment(setting.Key, value);
    }
}

// A request's detail in /admin links to its trace in the Aspire dashboard (spec 017). The dashboard is served on the AppHost's own
// first https address; without one the admin page shows the trace id only.
var dashboardUrl = (builder.Configuration["ASPNETCORE_URLS"] ?? "").Split(';').FirstOrDefault(u => u.StartsWith("https://", StringComparison.Ordinal));
if (dashboardUrl is not null)
{
    api.WithEnvironment("Admin__TraceUrlTemplate", $"{dashboardUrl.TrimEnd('/')}/traces/detail/{{traceId}}");
}

if (hostedChat)
{
    var apiKey = builder.AddParameter("openai-api-key", secret: true);
    api.WithEnvironment("Chat__Provider", "OpenAICompatible")
        .WithEnvironment("Chat__OpenAICompatible__Model", chatModel)
        .WithEnvironment("Chat__OpenAICompatible__ApiKey", apiKey);
    if (builder.Configuration["Parameters:openai-endpoint"] is { Length: > 0 } endpoint)
    {
        api.WithEnvironment("Chat__OpenAICompatible__Endpoint", endpoint);
    }
}
else if (chat is null)
{
    api.WithEnvironment("Chat__Provider", "Demo");
}
else
{
    // Not waited for, like the embedding model: an answer fails with a retryable error until the model is pulled.
    api.WithReference(chat);
}

builder.AddProject<Projects.RagAssistant_Web>("web")
    .WithReference(api)
    .WithReference(keycloak)
    .WithEnvironment("Keycloak__ClientSecret", WebClientSecret)
    .WaitFor(api)
    .WaitFor(keycloak)
    .WithExternalHttpEndpoints();

await builder.Build().RunAsync();
