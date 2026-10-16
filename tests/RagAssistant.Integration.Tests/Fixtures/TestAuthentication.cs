using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace RagAssistant.Integration.Tests.Fixtures;

internal static class TestAuthentication
{
    public static void Configure(IServiceCollection services)
    {
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
        });
    }
}
