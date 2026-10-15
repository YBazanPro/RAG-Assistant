using RagAssistant.SharedKernel.Persistence;

namespace RagAssistant.MigrationService;

internal sealed partial class Worker(
    DatabaseMigrator migrator,
    IHostApplicationLifetime lifetime,
    ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await migrator.MigrateAsync(stoppingToken);
        }
        // Process boundary: any failure must surface as a non-zero exit code so the AppHost
        // keeps the API from starting (WaitForCompletion).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogMigrationFailed(logger, ex);
            Environment.ExitCode = 1;
        }

        lifetime.StopApplication();
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "Database migration failed")]
    private static partial void LogMigrationFailed(ILogger logger, Exception exception);
}
