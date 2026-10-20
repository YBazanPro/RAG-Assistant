using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using RagAssistant.Integration.Tests.Sample;
using RagAssistant.SharedKernel.Persistence;
using TUnit.Core.Interfaces;

namespace RagAssistant.Integration.Tests.Fixtures;

public sealed class RagAssistantAppFactory : WebApplicationFactory<Program>, IAsyncInitializer
{
    [ClassDataSource<PostgresFixture>(Shared = SharedType.PerTestSession)]
    public required PostgresFixture Postgres { get; init; }

    public CapturingLoggerProvider Logs { get; } = new();

    /// <summary>A private original-file store per factory, removed with it, so tests never share or pollute the dev folder.</summary>
    public string StoragePath { get; } = Path.Combine(Path.GetTempPath(), "ragassistant-tests", Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DatabaseMigrator>().MigrateAsync(CancellationToken.None);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(StoragePath))
        {
            try
            {
                Directory.Delete(StoragePath, recursive: true);
            }
            catch (IOException)
            {
                // Best effort: a leftover temp folder is harmless.
            }
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:ragassistant", Postgres.ConnectionString);
        builder.UseSetting("Documents:Storage:Path", StoragePath);
        // Suggested questions (spec 020) would call the shared fake chat model after every ingestion; their own tests turn them on.
        builder.UseSetting("Documents:Suggestions:Enabled", "false");
        builder.UseSetting("Logging:LogLevel:Microsoft.AspNetCore.Diagnostics", "Debug");
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
        builder.ConfigureServices(services =>
        {
            services.AddDatabaseMigrator();
            services.AddSampleSlice();

            // Paused by default: the worker's probe fails, so spec 006 imports stay queued. Ingestion tests swap in a working fake.
            services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>, UnavailableEmbeddingGenerator>();

            // Shared by every test on this factory, so tests that script the model use a ChatBed of their own.
            services.AddSingleton<IChatClient, ScriptedChatClient>();

            TestAuthentication.Configure(services);
        });
    }
}
