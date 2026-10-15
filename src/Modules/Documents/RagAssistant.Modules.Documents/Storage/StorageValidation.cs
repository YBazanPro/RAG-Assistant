using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RagAssistant.Modules.Documents.Options;

namespace RagAssistant.Modules.Documents.Storage;

/// <summary>Creates the store directories and proves they are writable, so a bad path stops the API at startup (spec 010).</summary>
internal sealed class StorageOptionsValidator(IHostEnvironment? environment = null) : IValidateOptions<StorageOptions>
{
    public ValidateOptionsResult Validate(string? name, StorageOptions options)
    {
        var root = StorageOptions.ResolveRoot(options.Path, environment?.ContentRootPath);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, FileSystemOriginalFileStore.PdfDirectory));
            Directory.CreateDirectory(Path.Combine(root, FileSystemOriginalFileStore.TempDirectory));

            var probe = Path.Combine(root, FileSystemOriginalFileStore.TempDirectory, $".probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            return ValidateOptionsResult.Success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return ValidateOptionsResult.Fail($"Documents:Storage:Path '{root}' is not a writable directory: {ex.Message}");
        }
    }
}

/// <summary>At startup, removes staged uploads that a crash left behind.</summary>
internal sealed partial class StagedFileSweeper(IOriginalFileStore store, ILogger<StagedFileSweeper> logger) : IHostedService
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(1);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var deleted = store.DeleteStaleStagedFiles(MaxAge);
        if (deleted > 0)
        {
            LogSwept(logger, deleted);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed {Count} stale staged upload(s) from the file store")]
    private static partial void LogSwept(ILogger logger, int count);
}
