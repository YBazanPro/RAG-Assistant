using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RagAssistant.Modules.Evaluation.Domain;

namespace RagAssistant.Modules.Evaluation.Persistence.Configurations;

internal sealed class EvaluationRunConfiguration : IEntityTypeConfiguration<EvaluationRun>
{
    public const int MaxIdLength = 100;

    public void Configure(EntityTypeBuilder<EvaluationRun> builder)
    {
        builder.ToTable("runs");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasMaxLength(MaxIdLength).ValueGeneratedNever();
        builder.Property(r => r.ChatModel).HasMaxLength(200);
        builder.Property(r => r.EmbeddingModel).HasMaxLength(200);
        builder.Property(r => r.JudgeModel).HasMaxLength(200);
        builder.Property(r => r.RetrievalMode).HasMaxLength(16);
        builder.Property(r => r.DatasetHash).HasMaxLength(64);
        builder.Property(r => r.CorpusHash).HasMaxLength(64);
        builder.Property(r => r.GitCommit).HasMaxLength(64);
        builder.Property(r => r.UploadedBy).HasMaxLength(200);
        builder.Property(r => r.Configuration).HasColumnType("jsonb");
        builder.Property(r => r.Metrics).HasColumnType("jsonb");
        builder.Property(r => r.Judge).HasColumnType("jsonb");
        builder.Property(r => r.Cases).HasColumnType("jsonb");

        // The admin list: newest first, optionally for one chat model.
        builder.HasIndex(r => new { r.StartedAt, r.Id }).IsDescending(true, false);
        builder.HasIndex(r => r.ChatModel);
    }
}
