using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RagAssistant.Modules.Documents.Domain;

namespace RagAssistant.Modules.Documents.Persistence.Configurations;

internal sealed class IngestionRunConfiguration : IEntityTypeConfiguration<IngestionRun>
{
    public void Configure(EntityTypeBuilder<IngestionRun> builder)
    {
        builder.ToTable("ingestion_runs");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.ErrorCode).HasMaxLength(100);
        builder.Property(r => r.ErrorMessage).HasMaxLength(2000);

        builder.HasIndex(r => new { r.VersionId, r.Attempt }).IsUnique();
        builder.HasIndex(r => r.Status);
        builder.HasMany(r => r.Events).WithOne().HasForeignKey(e => e.RunId).OnDelete(DeleteBehavior.Restrict);
    }
}
