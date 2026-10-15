using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;
using RagAssistant.Integration.Tests.Fixtures;

namespace RagAssistant.Integration.Tests.Documents;

// Spec 010, AC-7: the file store is validated and swept at startup.
[ClassDataSource<RagAssistantAppFactory>(Shared = SharedType.PerTestSession)]
public sealed class PdfStorageStartupTests(RagAssistantAppFactory factory)
{
    [Test]
    public async Task Startup_storagePathIsNotAWritableDirectory_failsWithAnOptionsValidationError()
    {
        var blocker = Path.Combine(Path.GetTempPath(), "ragassistant-tests", $"blocker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.GetDirectoryName(blocker)!);
        await File.WriteAllTextAsync(blocker, "a file where the store directory should be");
        try
        {
            await using var misconfigured = factory.WithWebHostBuilder(b => b.UseSetting("Documents:Storage:Path", blocker));

            var ex = await Assert.That(() => misconfigured.CreateClient()).Throws<OptionsValidationException>();

            await Assert.That(ex!.Message).Contains("Documents:Storage:Path");
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Test]
    public async Task Startup_staleStagedUploads_areDeletedAndFreshOnesKept()
    {
        var path = Path.Combine(Path.GetTempPath(), "ragassistant-tests", $"sweep-{Guid.NewGuid():N}");
        var tmp = Path.Combine(path, "tmp");
        Directory.CreateDirectory(tmp);
        var stale = Path.Combine(tmp, "stale");
        var fresh = Path.Combine(tmp, "fresh");
        await File.WriteAllTextAsync(stale, "x");
        await File.WriteAllTextAsync(fresh, "x");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-2));
        try
        {
            await using var started = factory.WithWebHostBuilder(b => b.UseSetting("Documents:Storage:Path", path));
            using var client = started.CreateClient();

            await Assert.That(File.Exists(stale)).IsFalse();
            await Assert.That(File.Exists(fresh)).IsTrue();
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Test]
    public async Task Startup_defaultFactory_usesItsOwnTemporaryStoreWithBothDirectories()
    {
        using var client = factory.CreateClient();

        await Assert.That(Directory.Exists(Path.Combine(factory.StoragePath, "pdf"))).IsTrue();
        await Assert.That(Directory.Exists(Path.Combine(factory.StoragePath, "tmp"))).IsTrue();
    }
}
