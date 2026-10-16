using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RagAssistant.Modules.Chat.Domain;

namespace RagAssistant.Modules.Chat.Persistence.Configurations;

internal sealed class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    public void Configure(EntityTypeBuilder<Conversation> builder)
    {
        builder.ToTable("conversations");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.OwnerSub).HasMaxLength(200);
        builder.Property(c => c.Title).HasMaxLength(100);

        // The owner's list, newest activity first, with the id as tie-breaker.
        builder.HasIndex(c => new { c.OwnerSub, c.LastActivityAt, c.Id }).IsDescending(false, true, false);
        builder.HasMany(c => c.Messages).WithOne().HasForeignKey(m => m.ConversationId).OnDelete(DeleteBehavior.Cascade);
    }
}
