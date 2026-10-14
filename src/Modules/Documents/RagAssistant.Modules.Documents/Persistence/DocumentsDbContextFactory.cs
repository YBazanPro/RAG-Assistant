using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace RagAssistant.Modules.Documents.Persistence;

/// <summary>
/// Used by <c>dotnet ef</c> only. It lets the tools build the model without starting the Host,
/// which would need the Aspire-provided connection string; nothing connects to this database.
/// </summary>
internal sealed class DocumentsDbContextFactory : IDesignTimeDbContextFactory<DocumentsDbContext>
{
    public DocumentsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<DocumentsDbContext>()
            .UseNpgsql("Host=localhost;Database=ragassistant", npgsql =>
            {
                npgsql.UseVector();
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", DocumentsDbContext.Schema);
            })
            .Options;
        return new DocumentsDbContext(options);
    }
}
