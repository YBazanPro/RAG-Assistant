namespace RagAssistant.Modules.Documents.Storage;

/// <summary>Where the original binary of a version lives, outside the database (ADR 0006). Content-addressed and never deleted.</summary>
internal interface IOriginalFileStore
{
    /// <summary>
    /// Streams <paramref name="content"/> to a temporary area, hashing as it goes. Returns null, leaving nothing behind, when the
    /// content is longer than <paramref name="maxBytes"/>. The caller disposes the result, which discards an uncommitted file.
    /// </summary>
    Task<StagedFile?> StageAsync(Stream content, long maxBytes, CancellationToken cancellationToken);

    /// <summary>Opens a stored file for reading, or returns null when the key is unknown or the file is missing.</summary>
    Stream? OpenRead(string storageKey);

    /// <summary>Deletes staged files older than <paramref name="age"/> (leftovers of crashed uploads). Returns how many were removed.</summary>
    int DeleteStaleStagedFiles(TimeSpan age);
}

/// <summary>An upload that has been received and hashed but is not yet part of the store.</summary>
internal abstract class StagedFile(string sha256, long length) : IAsyncDisposable
{
    /// <summary>Lowercase hex SHA-256 of the file bytes.</summary>
    public string Sha256 { get; } = sha256;

    public long Length { get; } = length;

    /// <summary>Opens the staged bytes, e.g. for the PDF probe, before deciding to keep them.</summary>
    public abstract Stream OpenRead();

    /// <summary>Moves the file to its content-addressed place (a no-op if the same content is already stored) and returns its storage key.</summary>
    public abstract Task<string> CommitAsync(CancellationToken cancellationToken);

    public abstract ValueTask DisposeAsync();
}
