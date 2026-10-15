using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RagAssistant.SharedKernel.Persistence;

/// <summary>
/// Applies the migrations of every registered module database, in registration order.
/// Used by the MigrationService and by the integration-test fixture so both run identical migrations.
/// </summary>
public sealed partial class DatabaseMigrator(
    IEnumerable<IMigrationTarget> targets,
    IServiceScopeFactory scopeFactory,
    ILogger<DatabaseMigrator> logger)
{
    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        var targetList = targets.ToList();
        if (targetList.Count == 0)
        {
            LogNoTargets(logger);
            return;
        }

        foreach (var target in targetList)
        {
            LogMigrating(logger, target.Name);
            await using var scope = scopeFactory.CreateAsyncScope();
            await target.MigrateAsync(scope.ServiceProvider, cancellationToken);
            LogMigrated(logger, target.Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "No module databases registered; nothing to migrate")]
    private static partial void LogNoTargets(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Applying migrations for {Target}")]
    private static partial void LogMigrating(ILogger logger, string target);

    [LoggerMessage(Level = LogLevel.Information, Message = "Migrations applied for {Target}")]
    private static partial void LogMigrated(ILogger logger, string target);
}
