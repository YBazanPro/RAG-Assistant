using RagAssistant.SharedKernel.Common;

namespace RagAssistant.Modules.Documents.Common;

internal static class DocumentErrors
{
    public static readonly Error NotFound = Error.NotFound("documents.not_found", "The document was not found.");

    public static readonly Error VisibilityOwnerOnly =
        Error.Forbidden("documents.visibility_owner_only", "Only the person who imported a document can change who can read it.");

    public static readonly Error VersionNotFound = Error.NotFound("documents.version_not_found", "The version was not found.");

    public static readonly Error PassageNotFound = Error.NotFound("documents.passage_not_found", "The passage was not found in this version.");

    public static readonly Error Archived = Error.Conflict("documents.archived", "The document is archived. Restore it first.");

    public static readonly Error IngestionInProgress =
        Error.Conflict("documents.ingestion_in_progress", "The latest version is still being indexed. Try again when it has finished.");

    public static readonly Error NotReprocessable =
        Error.Conflict("documents.not_reprocessable", "Only a document whose latest version is indexed can be reprocessed.");

    public static readonly Error NotIndexed =
        Error.Conflict("documents.not_indexed", "The document has no indexed version yet. Try again when it has finished.");

    public static readonly Error AlreadyArchived = Error.Conflict("documents.already_archived", "The document is already archived.");

    public static readonly Error NotArchived = Error.Conflict("documents.not_archived", "The document is not archived.");

    public static readonly Error PreconditionRequired =
        Error.PreconditionRequired("documents.precondition_required", "An If-Match header with the document's current ETag is required.");

    public static Error EditConflict(int? currentRevision) =>
        Error.PreconditionFailed(
            "documents.edit_conflict",
            "The document was changed by someone else. Reload it and try again.",
            currentRevision is null ? null : new Dictionary<string, object?> { ["currentRevision"] = currentRevision });

    public static Error DuplicateContent(Guid? existingId) =>
        Error.Conflict(
            "documents.duplicate_content",
            "A document with identical content already exists.",
            existingId is null ? null : new Dictionary<string, object?> { ["existingDocumentId"] = existingId });
}
