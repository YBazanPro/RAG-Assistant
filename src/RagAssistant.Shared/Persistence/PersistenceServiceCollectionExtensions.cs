using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace RagAssistant.Shared.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Registers a module DbContext with the migration runner. Call from the module's public
    /// <c>AddXxxPersistence</c> method, after the DbContext itself is registered.
    /// </summary>
    public static IServiceCollection AddMigrationTarget<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        services.AddSingleton<IMigrationTarget, MigrationTarget<TContext>>();
        return services;
    }

    public static IServiceCollection AddDatabaseMigrator(this IServiceCollection services)
    {
        services.AddSingleton<DatabaseMigrator>();
        return services;
    }
}
