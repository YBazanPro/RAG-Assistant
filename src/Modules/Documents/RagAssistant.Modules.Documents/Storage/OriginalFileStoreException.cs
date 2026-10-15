namespace RagAssistant.Modules.Documents.Storage;

/// <summary>
/// The store could not write: a full, missing or unwritable disk. It is a server fault, so it must never be mistaken for the
/// <see cref="IOException"/> of a request body that ended early, which is the caller's own doing.
/// </summary>
internal sealed class OriginalFileStoreException(string message, Exception inner) : Exception(message, inner);
