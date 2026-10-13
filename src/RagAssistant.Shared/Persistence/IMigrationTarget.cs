namespace RagAssistant.Shared.Persistence;

/// <summary>
/// A module database whose EF Core migrations are applied by <see cref="DatabaseMigrator"/>.
/// Register through <see cref="PersistenceServiceCollectionExtensions.AddMigrationTarget{TContext}"/>.
/// </summary>
public interface IMigrationTarget
{
    string Name { get; }

    Task MigrateAsync(IServiceProvider scopedServices, CancellationToken cancellationToken);
}
