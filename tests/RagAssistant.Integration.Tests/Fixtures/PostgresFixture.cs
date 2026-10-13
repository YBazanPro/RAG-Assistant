using Testcontainers.PostgreSql;
using TUnit.Core.Interfaces;

namespace RagAssistant.Integration.Tests.Fixtures;

public sealed class PostgresFixture : IAsyncInitializer, IAsyncDisposable
{
    // Keep in sync with PostgresImageTag in src/RagAssistant.AppHost/AppHost.cs.
    public const string Image = "pgvector/pgvector:0.8.7-pg18-trixie";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image)
        .WithDatabase("ragassistant")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
