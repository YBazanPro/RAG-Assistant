using RagAssistant.Shared.Common;

namespace RagAssistant.Modules.Documents.Common;

internal static class DocumentErrors
{
    public static readonly Error NotFound = Error.NotFound("documents.not_found", "The document was not found.");
}
