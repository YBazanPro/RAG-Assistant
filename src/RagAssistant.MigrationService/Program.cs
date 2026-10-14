using RagAssistant.MigrationService;
using RagAssistant.Modules.Documents;
using RagAssistant.Shared.Persistence;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddNpgsqlDataSource("ragassistant");

builder.Services.AddDatabaseMigrator();
// Register each module's persistence here so its migrations run.
builder.Services.AddDocumentsPersistence(builder.Configuration);

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
await host.RunAsync();
