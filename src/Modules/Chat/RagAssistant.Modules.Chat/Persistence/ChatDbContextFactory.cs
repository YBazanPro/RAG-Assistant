using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace RagAssistant.Modules.Chat.Persistence;

/// <summary>
/// Used by <c>dotnet ef</c> only. It lets the tools build the model without starting the API,
/// which would need the Aspire-provided connection string; nothing connects to this database.
/// </summary>
internal sealed class ChatDbContextFactory : IDesignTimeDbContextFactory<ChatDbContext>
{
    public ChatDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ChatDbContext>()
            .UseNpgsql("Host=localhost;Database=ragassistant", npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", ChatDbContext.Schema))
            .Options;
        return new ChatDbContext(options);
    }
}
