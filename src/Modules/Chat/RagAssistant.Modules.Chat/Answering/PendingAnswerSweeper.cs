using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RagAssistant.Modules.Chat.Common;
using RagAssistant.Modules.Chat.Domain;
using RagAssistant.Modules.Chat.Persistence;

namespace RagAssistant.Modules.Chat.Answering;

/// <summary>
/// At startup, answers left pending by the previous process can no longer finish: they become failed with <c>chat.interrupted</c>,
/// which the user can retry (spec 012). Only answers started before this process are touched. Single instance, like ingestion.
/// </summary>
internal sealed partial class PendingAnswerSweeper(IServiceScopeFactory scopes, TimeProvider clock, ILogger<PendingAnswerSweeper> logger)
    : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly DateTimeOffset _startedAt = clock.GetUtcNow();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // The API starts after the MigrationService, but a database that is briefly unreachable must not take the whole host down.
            while (true)
            {
                try
                {
                    var swept = await SweepAsync(stoppingToken);
                    if (swept > 0)
                    {
                        LogSwept(logger, swept);
                    }

                    return;
                }
                catch (DbException ex)
                {
                    LogSweepFailed(logger, ex, RetryDelay.TotalSeconds);
                    await Task.Delay(RetryDelay, clock, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var now = clock.GetUtcNow();
        return await db.Messages
            .Where(m => m.Status == AnswerStatus.Pending && m.PendingSince < _startedAt)
            .ExecuteUpdateAsync(
                s => s.SetProperty(m => m.Status, AnswerStatus.Failed)
                    .SetProperty(m => m.ErrorCode, ChatErrors.InterruptedCode)
                    .SetProperty(m => m.CompletedAt, now),
                cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Marked {Count} answers interrupted by the previous shutdown as failed")]
    private static partial void LogSwept(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not sweep pending answers; retrying in {DelaySeconds} s")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception, double delaySeconds);
}
