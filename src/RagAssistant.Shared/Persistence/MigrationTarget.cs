using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace RagAssistant.Shared.Persistence;

internal sealed class MigrationTarget<TContext> : IMigrationTarget
    where TContext : DbContext
{
    public string Name => typeof(TContext).Name;

    public Task MigrateAsync(IServiceProvider scopedServices, CancellationToken cancellationToken) =>
        scopedServices.GetRequiredService<TContext>().Database.MigrateAsync(cancellationToken);
}
