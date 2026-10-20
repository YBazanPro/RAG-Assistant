using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace RagAssistant.Modules.Evaluation.Persistence;

/// <summary>
/// Used by <c>dotnet ef</c> only. It lets the tools build the model without starting the API,
/// which would need the Aspire-provided connection string; nothing connects to this database.
/// </summary>
internal sealed class EvaluationDbContextFactory : IDesignTimeDbContextFactory<EvaluationDbContext>
{
    public EvaluationDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<EvaluationDbContext>()
            .UseNpgsql("Host=localhost;Database=ragassistant", npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", EvaluationDbContext.Schema))
            .Options;
        return new EvaluationDbContext(options);
    }
}
