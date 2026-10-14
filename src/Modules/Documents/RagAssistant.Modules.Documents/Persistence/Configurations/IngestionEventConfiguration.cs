using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RagAssistant.Modules.Documents.Domain;

namespace RagAssistant.Modules.Documents.Persistence.Configurations;

internal sealed class IngestionEventConfiguration : IEntityTypeConfiguration<IngestionEvent>
{
    public void Configure(EntityTypeBuilder<IngestionEvent> builder)
    {
        builder.ToTable("ingestion_events");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).UseIdentityAlwaysColumn();
        builder.Property(e => e.Message).HasMaxLength(2000);

        builder.HasIndex(e => new { e.RunId, e.Id });
    }
}
