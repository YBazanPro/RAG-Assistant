using Microsoft.EntityFrameworkCore;
using RagAssistant.Modules.Chat.Domain;
using RagAssistant.SharedKernel.Persistence;

namespace RagAssistant.Modules.Chat.Persistence;

internal sealed class ChatDbContext(DbContextOptions<ChatDbContext> options) : DbContext(options)
{
    public const string Schema = "chat";

    public DbSet<Conversation> Conversations => Set<Conversation>();

    public DbSet<Message> Messages => Set<Message>();

    public DbSet<CitationSnapshot> CitationSnapshots => Set<CitationSnapshot>();

    public DbSet<RequestTrace> RequestTraces => Set<RequestTrace>();

    public DbSet<MessageRating> MessageRatings => Set<MessageRating>();

    public DbSet<IssueReport> IssueReports => Set<IssueReport>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ChatDbContext).Assembly);
        modelBuilder.UseSnakeCaseColumns();
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(32);
    }
}
