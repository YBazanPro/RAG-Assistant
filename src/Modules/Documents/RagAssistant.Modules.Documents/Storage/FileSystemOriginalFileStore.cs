using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RagAssistant.Modules.Documents.Options;

namespace RagAssistant.Modules.Documents.Storage;

/// <summary>Layout: <c>pdf/&lt;sha256&gt;.pdf</c> for kept files, <c>tmp/&lt;guid&gt;</c> for uploads in flight.</summary>
internal sealed partial class FileSystemOriginalFileStore : IOriginalFileStore
{
    public const string PdfDirectory = "pdf";
    public const string TempDirectory = "tmp";

    private const int BufferSize = 81920;

    private readonly string _root;
    private readonly TimeProvider _clock;

    public FileSystemOriginalFileStore(IOptions<StorageOptions> options, TimeProvider clock, IHostEnvironment? environment = null)
    {
        _root = StorageOptions.ResolveRoot(options.Value.Path, environment?.ContentRootPath);
        _clock = clock;
    }

    public static string PdfKey(string sha256) => $"{PdfDirectory}/{sha256}.pdf";

    public async Task<StagedFile?> StageAsync(Stream content, long maxBytes, CancellationToken cancellationToken)
    {
        var tempPath = Path.Combine(_root, TempDirectory, Guid.NewGuid().ToString("N"));
        var keep = false;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long length = 0;
            // Failures of the disk become OriginalFileStoreException; failures of reading `content` (the request) keep their own type,
            // so a caller can tell a broken store (a server fault) from a body that ended early (the client's).
            await using (var file = OpenForWrite(tempPath))
            {
                var buffer = new byte[BufferSize];
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    length += read;
                    if (length > maxBytes)
                    {
                        return null;
                    }

                    hash.AppendData(buffer, 0, read);
                    await WriteAsync(file, buffer.AsMemory(0, read), cancellationToken);
                }

                Flush(file);
            }

            keep = true;
            return new Staged(this, tempPath, Convert.ToHexStringLower(hash.GetHashAndReset()), length);
        }
        finally
        {
            if (!keep)
            {
                TryDelete(tempPath);
            }
        }
    }

    private FileStream OpenForWrite(string tempPath)
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(_root, TempDirectory));
            return new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception ex) when (IsDiskFailure(ex))
        {
            throw new OriginalFileStoreException("The original-file store could not create a file.", ex);
        }
    }

    private static async ValueTask WriteAsync(FileStream file, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        try
        {
            await file.WriteAsync(data, cancellationToken);
        }
        catch (Exception ex) when (IsDiskFailure(ex))
        {
            throw new OriginalFileStoreException("The original-file store could not write a file.", ex);
        }
    }

    private static void Flush(FileStream file)
    {
        try
        {
            file.Flush(flushToDisk: true);
        }
        catch (Exception ex) when (IsDiskFailure(ex))
        {
            throw new OriginalFileStoreException("The original-file store could not write a file.", ex);
        }
    }

    private static bool IsDiskFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or NotSupportedException;

    public Stream? OpenRead(string storageKey)
    {
        if (!KeyPattern().IsMatch(storageKey))
        {
            return null;
        }

        var path = Path.Combine(_root, PdfDirectory, storageKey[(PdfDirectory.Length + 1)..]);
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    public int DeleteStaleStagedFiles(TimeSpan age)
    {
        var directory = Path.Combine(_root, TempDirectory);
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var cutoff = _clock.GetUtcNow().UtcDateTime - age;
        var deleted = 0;
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            if (File.GetLastWriteTimeUtc(path) < cutoff && TryDelete(path))
            {
                deleted++;
            }
        }

        return deleted;
    }

    // Keys come from a hash, never from a client file name; anything else is refused before it can reach the file system.
    [GeneratedRegex("^pdf/[0-9a-f]{64}\\.pdf\\z")]
    private static partial Regex KeyPattern();

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a stray temp file is harmless and the next startup sweep retries it.
            return false;
        }
    }

    private sealed class Staged(FileSystemOriginalFileStore store, string tempPath, string sha256, long length) : StagedFile(sha256, length)
    {
        private bool _done;

        public override Stream OpenRead() =>
            new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

        public override Task<string> CommitAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = PdfKey(Sha256);
            var directory = Path.Combine(store._root, PdfDirectory);
            var finalPath = Path.Combine(directory, $"{Sha256}.pdf");
            Directory.CreateDirectory(directory);

            if (File.Exists(finalPath))
            {
                // Same content already stored (re-import, rollback, archived duplicate): keep the existing blob.
                TryDelete(tempPath);
            }
            else
            {
                try
                {
                    File.Move(tempPath, finalPath, overwrite: false);
                }
                catch (IOException) when (File.Exists(finalPath))
                {
                    // A concurrent upload of the same bytes won the rename; both are identical.
                    TryDelete(tempPath);
                }
            }

            _done = true;
            return Task.FromResult(key);
        }

        public override ValueTask DisposeAsync()
        {
            if (!_done)
            {
                TryDelete(tempPath);
                _done = true;
            }

            return ValueTask.CompletedTask;
        }
    }
}
