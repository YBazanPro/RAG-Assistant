using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RagAssistant.Modules.Chat.Domain;

namespace RagAssistant.Modules.Chat.Persistence.Configurations;

internal sealed class MessageRatingConfiguration : IEntityTypeConfiguration<MessageRating>
{
    public void Configure(EntityTypeBuilder<MessageRating> builder)
    {
        builder.ToTable("message_ratings");
        builder.HasKey(r => r.MessageId);
        builder.Property(r => r.MessageId).ValueGeneratedNever();

        // Deleting a conversation or a message removes its rating with it (spec 014).
        builder.HasOne<Message>().WithOne().HasForeignKey<MessageRating>(r => r.MessageId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class IssueReportConfiguration : IEntityTypeConfiguration<IssueReport>
{
    public void Configure(EntityTypeBuilder<IssueReport> builder)
    {
        builder.ToTable("issue_reports");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Comment).HasMaxLength(1000);

        builder.HasIndex(r => r.MessageId);
        builder.HasOne<Message>().WithMany().HasForeignKey(r => r.MessageId).OnDelete(DeleteBehavior.Cascade);
    }
}
