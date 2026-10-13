using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RagAssistant.Integration.Tests.Sample;
using RagAssistant.Shared.Persistence;
using TUnit.Core.Interfaces;

namespace RagAssistant.Integration.Tests.Fixtures;

public sealed class RagAssistantAppFactory : WebApplicationFactory<Program>, IAsyncInitializer
{
    [ClassDataSource<PostgresFixture>(Shared = SharedType.PerTestSession)]
    public required PostgresFixture Postgres { get; init; }

    public CapturingLoggerProvider Logs { get; } = new();

    public async Task InitializeAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DatabaseMigrator>().MigrateAsync(CancellationToken.None);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:ragassistant", Postgres.ConnectionString);
        builder.UseSetting("Logging:LogLevel:Microsoft.AspNetCore.Diagnostics", "Debug");
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
        builder.ConfigureServices(services =>
        {
            services.AddDatabaseMigrator();
            services.AddSampleSlice();
        });
    }
}
