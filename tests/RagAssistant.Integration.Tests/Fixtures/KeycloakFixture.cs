using Testcontainers.Keycloak;
using TUnit.Core.Interfaces;

namespace RagAssistant.Integration.Tests.Fixtures;

public sealed class KeycloakFixture : IAsyncInitializer, IAsyncDisposable
{
    // Keep in sync with KeycloakImageTag in src/RagAssistant.AppHost/AppHost.cs.
    public const string Image = "quay.io/keycloak/keycloak:26.8";

    public const string RealmFileName = "ragassistant-realm.json";

    private readonly KeycloakContainer _container = new KeycloakBuilder(Image)
        .WithResourceMapping(
            Path.Combine(AppContext.BaseDirectory, RealmFileName),
            "/opt/keycloak/data/import/")
        .WithCommand("--import-realm")
        .Build();

    public string BaseAddress => _container.GetBaseAddress();

    public Task InitializeAsync() => _container.StartAsync();

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
