using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RagAssistant.Modules.Documents.Domain;

namespace RagAssistant.Modules.Documents.Persistence.Configurations;

internal sealed class DocumentConfiguration : IEntityTypeConfiguration<Document>
{
    public const string ActiveContentHashIndex = "ux_documents_active_content_hash";

    public void Configure(EntityTypeBuilder<Document> builder)
    {
        builder.ToTable("documents");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();
        builder.Property(d => d.Revision).IsConcurrencyToken().HasDefaultValue(1);
        builder.Property(d => d.Title).HasMaxLength(200);
        builder.Property(d => d.OwnerName).HasMaxLength(100);
        builder.Property(d => d.Language).HasMaxLength(2);
        builder.Property(d => d.Source).HasMaxLength(500);
        builder.Property(d => d.LatestContentHash).HasMaxLength(64);
        builder.Property(d => d.CreatedBy).HasMaxLength(128);
        builder.Property(d => d.UpdatedBy).HasMaxLength(128);

        builder.HasIndex(d => d.Tags).HasMethod("gin");
        builder.HasIndex(d => d.LatestContentHash)
            .IsUnique()
            .HasFilter("status = 'Active'")
            .HasDatabaseName(ActiveContentHashIndex);

        builder.HasMany(d => d.Versions).WithOne().HasForeignKey(v => v.DocumentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<DocumentVersion>().WithMany().HasForeignKey(d => d.ActiveVersionId).OnDelete(DeleteBehavior.Restrict);
    }
}
