using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RagAssistant.Modules.Chat.Domain;

namespace RagAssistant.Modules.Chat.Persistence.Configurations;

internal sealed class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> builder)
    {
        builder.ToTable("messages");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();
        builder.Property(m => m.FilterLanguage).HasMaxLength(2);
        builder.Property(m => m.ErrorCode).HasMaxLength(100);
        builder.Property(m => m.RetrievalQuery).HasMaxLength(1000);

        builder.HasIndex(m => new { m.ConversationId, m.Sequence }).IsUnique();

        // At most one answer in flight per conversation; backs chat.answer_in_progress at the database level.
        builder.HasIndex(m => m.ConversationId)
            .HasDatabaseName("ix_messages_one_pending_per_conversation")
            .IsUnique()
            .HasFilter("status = 'Pending'");

        // The startup sweep looks for pending answers only.
        builder.HasIndex(m => m.Status)
            .HasDatabaseName("ix_messages_pending")
            .HasFilter("status = 'Pending'");

        builder.HasMany(m => m.Citations).WithOne().HasForeignKey(c => c.MessageId).OnDelete(DeleteBehavior.Cascade);
    }
}
