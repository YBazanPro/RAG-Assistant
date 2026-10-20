using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RagAssistant.Modules.Documents.Domain;
using RagAssistant.SharedKernel.Common.Text;

namespace RagAssistant.Modules.Documents.Persistence.Configurations;

internal sealed class PassageConfiguration : IEntityTypeConfiguration<Passage>
{
    public void Configure(EntityTypeBuilder<Passage> builder)
    {
        builder.ToTable("passages");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.TextHash).HasMaxLength(PassageTextHash.Length);
        builder.Property(p => p.Embedding).HasColumnType($"vector({DocumentsDbContext.EmbeddingDimensions})");

        builder.HasIndex(p => new { p.VersionId, p.Position }).IsUnique();
        // Citation source states (spec 020): "does the active version hold this text?"
        builder.HasIndex(p => new { p.VersionId, p.TextHash });
        builder.HasIndex(p => p.Embedding).HasMethod("hnsw").HasOperators("vector_cosine_ops");
    }
}
