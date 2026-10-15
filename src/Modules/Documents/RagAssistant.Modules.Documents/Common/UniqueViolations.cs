using Npgsql;
using RagAssistant.Modules.Documents.Persistence.Configurations;

namespace RagAssistant.Modules.Documents.Common;

internal static class UniqueViolations
{
    public static bool IsActiveContentHash(Exception ex) => Is(ex, DocumentConfiguration.ActiveContentHashIndex);

    public static bool IsVersionNumber(Exception ex) => Is(ex, DocumentVersionConfiguration.NumberIndex);

    // SaveChanges wraps the provider exception, ExecuteUpdate may not.
    private static bool Is(Exception ex, string constraint) =>
        (ex as PostgresException ?? ex.InnerException as PostgresException) is
        { SqlState: PostgresErrorCodes.UniqueViolation } pg && pg.ConstraintName == constraint;
}
