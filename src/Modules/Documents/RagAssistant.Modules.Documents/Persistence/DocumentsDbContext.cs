using Microsoft.EntityFrameworkCore;
using RagAssistant.Modules.Documents.Domain;
using RagAssistant.SharedKernel.Persistence;

namespace RagAssistant.Modules.Documents.Persistence;

internal sealed class DocumentsDbContext(DbContextOptions<DocumentsDbContext> options) : DbContext(options)
{
    public const string Schema = "documents";

    /// <summary>Width of <c>passages.embedding</c>; <see cref="Options.EmbeddingOptions.Dimensions"/> must match.</summary>
    public const int EmbeddingDimensions = 1024;

    public DbSet<Document> Documents => Set<Document>();

    public DbSet<DocumentVersion> Versions => Set<DocumentVersion>();

    public DbSet<Passage> Passages => Set<Passage>();

    public DbSet<IngestionRun> IngestionRuns => Set<IngestionRun>();

    public DbSet<IngestionEvent> IngestionEvents => Set<IngestionEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.HasPostgresExtension("vector");
        // Keyword search (spec 019): the french_unaccent configuration folds accents before stemming.
        modelBuilder.HasPostgresExtension("unaccent");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DocumentsDbContext).Assembly);
        modelBuilder.UseSnakeCaseColumns();
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(32);
    }
}
