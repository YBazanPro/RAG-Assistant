using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RagAssistant.Modules.Documents.Domain;

namespace RagAssistant.Modules.Documents.Persistence.Configurations;

internal sealed class PassageConfiguration : IEntityTypeConfiguration<Passage>
{
    public void Configure(EntityTypeBuilder<Passage> builder)
    {
        builder.ToTable("passages");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.Embedding).HasColumnType($"vector({DocumentsDbContext.EmbeddingDimensions})");

        builder.HasIndex(p => new { p.VersionId, p.Position }).IsUnique();
        builder.HasIndex(p => p.Embedding).HasMethod("hnsw").HasOperators("vector_cosine_ops");
    }
}
