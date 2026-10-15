using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RagAssistant.Modules.Documents.Domain;

namespace RagAssistant.Modules.Documents.Persistence.Configurations;

internal sealed class DocumentVersionConfiguration : IEntityTypeConfiguration<DocumentVersion>
{
    public const string NumberIndex = "IX_document_versions_document_id_number";

    public void Configure(EntityTypeBuilder<DocumentVersion> builder)
    {
        builder.ToTable("document_versions");
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).ValueGeneratedNever();
        builder.Property(v => v.OriginalFileName).HasMaxLength(255);
        builder.Property(v => v.ContentType).HasMaxLength(200);
        builder.Property(v => v.ContentHash).HasMaxLength(64);
        builder.Property(v => v.CreatedBy).HasMaxLength(128);
        builder.Property(v => v.EmbeddingModel).HasMaxLength(100);

        builder.HasIndex(v => new { v.DocumentId, v.Number }).IsUnique().HasDatabaseName(NumberIndex);
        builder.HasMany(v => v.Runs).WithOne().HasForeignKey(r => r.VersionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany<Passage>().WithOne().HasForeignKey(p => p.VersionId).OnDelete(DeleteBehavior.Restrict);
    }
}
