// Keep in sync with PostgresFixture.ImageTag in tests/RagAssistant.Integration.Tests.
const string PostgresImageTag = "0.8.7-pg18-trixie";
// Keep in sync with KeycloakFixture.ImageTag in tests/RagAssistant.Integration.Tests.
const string KeycloakImageTag = "26.8";
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

var migrations = builder.AddProject<Projects.RagAssistant_MigrationService>("migrations")
    .WithReference(database)
    .WaitFor(database);

var host = builder.AddProject<Projects.RagAssistant_Host>("host")
    .WithReference(database)
    .WithReference(keycloak)
    .WaitFor(database)
    .WaitFor(keycloak)
    .WaitForCompletion(migrations);

builder.AddProject<Projects.RagAssistant_Web>("web")
    .WithReference(host)
    .WithReference(keycloak)
    .WithEnvironment("Keycloak__ClientSecret", WebClientSecret)
    .WaitFor(host)
    .WaitFor(keycloak)
    .WithExternalHttpEndpoints();

await builder.Build().RunAsync();
