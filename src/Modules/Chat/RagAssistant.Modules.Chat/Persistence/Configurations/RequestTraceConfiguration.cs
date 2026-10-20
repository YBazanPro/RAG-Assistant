using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RagAssistant.Modules.Chat.Domain;

namespace RagAssistant.Modules.Chat.Persistence.Configurations;

internal sealed class RequestTraceConfiguration : IEntityTypeConfiguration<RequestTrace>
{
    public void Configure(EntityTypeBuilder<RequestTrace> builder)
    {
        builder.ToTable("request_traces");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.OwnerSub).HasMaxLength(200);
        builder.Property(t => t.FilterLanguage).HasMaxLength(2);
        builder.Property(t => t.QuestionLanguage).HasMaxLength(2);
        builder.Property(t => t.AnswerLanguage).HasMaxLength(2);
        builder.Property(t => t.RequestedLanguage).HasMaxLength(2);
        builder.Property(t => t.OtelTraceId).HasMaxLength(32);
        builder.Property(t => t.EvidenceOutcome).HasMaxLength(32);
        builder.Property(t => t.EmbeddingModel).HasMaxLength(200);
        builder.Property(t => t.ChatModel).HasMaxLength(200);
        builder.Property(t => t.PromptVersion).HasMaxLength(32);
        builder.Property(t => t.ErrorCode).HasMaxLength(100);
        builder.Property(t => t.RetrievalQuery).HasMaxLength(1000);
        builder.Property(t => t.RewritePromptVersion).HasMaxLength(32);
        builder.Property(t => t.RetrievedPassages).HasColumnType("jsonb");
        builder.Property(t => t.RetrievalMode).HasMaxLength(16);
        builder.Property(t => t.RerankStatus).HasMaxLength(16);
        builder.Property(t => t.RerankModel).HasMaxLength(200);

        builder.HasIndex(t => new { t.AssistantMessageId, t.Attempt }).IsUnique();
        builder.HasIndex(t => t.StartedAt);

        // Deleting a conversation (spec 014) removes its traces too: they hold the question text.
        builder.HasOne<Conversation>().WithMany().HasForeignKey(t => t.ConversationId).OnDelete(DeleteBehavior.Cascade);
    }
}
