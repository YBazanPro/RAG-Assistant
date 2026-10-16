using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RagAssistant.Modules.Chat.Domain;

namespace RagAssistant.Modules.Chat.Persistence.Configurations;

internal sealed class CitationSnapshotConfiguration : IEntityTypeConfiguration<CitationSnapshot>
{
    public void Configure(EntityTypeBuilder<CitationSnapshot> builder)
    {
        builder.ToTable("citation_snapshots");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.DocumentTitle).HasMaxLength(300);
        builder.HasIndex(c => c.MessageId);
    }
}
