using System.Text;
using Microsoft.EntityFrameworkCore;
using RagAssistant.Modules.Documents.Domain;

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
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DocumentsDbContext).Assembly);

        // snake_case columns, so raw SQL and index filters read naturally; set here rather than by a naming-convention package.
        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(e => e.GetProperties()))
        {
            property.SetColumnName(ToSnakeCase(property.Name));
        }
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(32);
    }

    private static string ToSnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0)
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(name[i]));
        }

        return builder.ToString();
    }
}
