using Microsoft.EntityFrameworkCore;
using RagAssistant.Modules.Evaluation.Domain;
using RagAssistant.SharedKernel.Persistence;

namespace RagAssistant.Modules.Evaluation.Persistence;

internal sealed class EvaluationDbContext(DbContextOptions<EvaluationDbContext> options) : DbContext(options)
{
    public const string Schema = "evaluation";

    public DbSet<EvaluationRun> Runs => Set<EvaluationRun>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(EvaluationDbContext).Assembly);
        modelBuilder.UseSnakeCaseColumns();
    }
}
