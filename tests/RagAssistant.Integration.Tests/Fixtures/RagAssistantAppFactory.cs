using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
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

            // Same issuer, audience and "roles" shape as Keycloak, but signed with a test key and with no metadata fetch.
            // Configure (not PostConfigure): the framework's post-configure would otherwise build a metadata manager from the Keycloak authority first.
            services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.Authority = null;
                options.MetadataAddress = null!;
                options.RequireHttpsMetadata = false;
                options.Configuration = new OpenIdConnectConfiguration();
                options.TokenValidationParameters.IssuerSigningKey = TestTokens.SigningKey;
                options.TokenValidationParameters.ValidIssuer = TestTokens.Issuer;
                options.TokenValidationParameters.ValidAudience = TestTokens.Audience;
                options.TokenValidationParameters.ClockSkew = TimeSpan.Zero;
            });
        });
    }
}
