using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RagAssistant.Modules.Documents.Domain;

namespace RagAssistant.Modules.Documents.Persistence.Configurations;

internal sealed class DocumentVersionConfiguration : IEntityTypeConfiguration<DocumentVersion>
{
    public const string NumberIndex = "IX_document_versions_document_id_number";
    public const string SourceCheck = "ck_document_versions_source";

    public void Configure(EntityTypeBuilder<DocumentVersion> builder)
    {
        // A PDF's original lives in the file store and a text format's source in the column, never both or neither (spec 010).
        builder.ToTable("document_versions", t => t.HasCheckConstraint(
            SourceCheck,
            "(source_format = 'Pdf' AND storage_key IS NOT NULL AND content IS NULL) " +
            "OR (source_format <> 'Pdf' AND content IS NOT NULL AND storage_key IS NULL)"));
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).ValueGeneratedNever();
        builder.Property(v => v.OriginalFileName).HasMaxLength(255);
        builder.Property(v => v.ContentType).HasMaxLength(200);
        builder.Property(v => v.StorageKey).HasMaxLength(300);
        builder.Property(v => v.ContentHash).HasMaxLength(64);
        builder.Property(v => v.CreatedBy).HasMaxLength(128);
        builder.Property(v => v.EmbeddingModel).HasMaxLength(100);
        builder.Property(v => v.DetectedLanguage).HasMaxLength(2);

        builder.HasIndex(v => new { v.DocumentId, v.Number }).IsUnique().HasDatabaseName(NumberIndex);
        builder.HasMany(v => v.Runs).WithOne().HasForeignKey(r => r.VersionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany<Passage>().WithOne().HasForeignKey(p => p.VersionId).OnDelete(DeleteBehavior.Restrict);
    }
}
