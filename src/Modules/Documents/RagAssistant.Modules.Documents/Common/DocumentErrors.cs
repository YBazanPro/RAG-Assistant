using RagAssistant.Shared.Common;

namespace RagAssistant.Modules.Documents.Common;

internal static class DocumentErrors
{
    public static readonly Error NotFound = Error.NotFound("documents.not_found", "The document was not found.");

    public static readonly Error VersionNotFound = Error.NotFound("documents.version_not_found", "The version was not found.");

    public static readonly Error PassageNotFound = Error.NotFound("documents.passage_not_found", "The passage was not found in this version.");
}
