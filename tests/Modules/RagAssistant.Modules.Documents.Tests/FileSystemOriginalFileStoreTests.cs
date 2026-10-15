using System.Security.Cryptography;
using RagAssistant.Modules.Documents.Options;
using RagAssistant.Modules.Documents.Storage;

namespace RagAssistant.Modules.Documents.Tests;

// Spec 010: the original file store (upload staging, content addressing, startup sweep and validation).
internal sealed class FileSystemOriginalFileStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ragassistant-unit", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private FileSystemOriginalFileStore CreateStore() =>
        new(Microsoft.Extensions.Options.Options.Create(new StorageOptions { Path = _root }), TimeProvider.System);

    private string TempDir => Path.Combine(_root, "tmp");

    private string PdfDir => Path.Combine(_root, "pdf");

    private static string HashOf(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    [Test]
    public async Task Stage_content_reportsHashAndLengthAndKeepsTheBytesInTmp()
    {
        var bytes = "%PDF-1.7 hello"u8.ToArray();
        var store = CreateStore();

        await using var staged = await store.StageAsync(new MemoryStream(bytes), 1024, CancellationToken.None);

        await Assert.That(staged).IsNotNull();
        await Assert.That(staged!.Sha256).IsEqualTo(HashOf(bytes));
        await Assert.That(staged.Length).IsEqualTo(bytes.Length);
        await using var read = staged.OpenRead();
        using var copy = new MemoryStream();
        await read.CopyToAsync(copy);
        await Assert.That(copy.ToArray()).IsEquivalentTo(bytes);
        await Assert.That(Directory.GetFiles(TempDir).Length).IsEqualTo(1);
    }

    [Test]
    public async Task Stage_contentExactlyAtTheLimit_isAccepted()
    {
        var store = CreateStore();

        await using var staged = await store.StageAsync(new MemoryStream(new byte[100]), 100, CancellationToken.None);

        await Assert.That(staged).IsNotNull();
        await Assert.That(staged!.Length).IsEqualTo(100L);
    }

    [Test]
    public async Task Stage_contentOverTheLimit_returnsNullAndLeavesNoTempFile()
    {
        var store = CreateStore();

        var staged = await store.StageAsync(new MemoryStream(new byte[101]), 100, CancellationToken.None);

        await Assert.That(staged).IsNull();
        await Assert.That(Directory.GetFiles(TempDir).Length).IsEqualTo(0);
    }

    [Test]
    public async Task Stage_cancelledMidUpload_removesTheTempFile()
    {
        var store = CreateStore();
        using var cts = new CancellationTokenSource();
        await using var source = new CancelAfterFirstReadStream(cts);

        await Assert.That(async () => await store.StageAsync(source, 1024 * 1024, cts.Token)).Throws<OperationCanceledException>();

        await Assert.That(Directory.GetFiles(TempDir).Length).IsEqualTo(0);
    }

    [Test]
    public async Task Commit_newContent_storesItUnderItsHashAndEmptiesTmp()
    {
        var bytes = "%PDF-1.7 content"u8.ToArray();
        var store = CreateStore();
        await using var staged = await store.StageAsync(new MemoryStream(bytes), 1024, CancellationToken.None);

        var key = await staged!.CommitAsync(CancellationToken.None);

        await Assert.That(key).IsEqualTo($"pdf/{HashOf(bytes)}.pdf");
        await Assert.That(await File.ReadAllBytesAsync(Path.Combine(PdfDir, $"{HashOf(bytes)}.pdf"))).IsEquivalentTo(bytes);
        await Assert.That(Directory.GetFiles(TempDir).Length).IsEqualTo(0);
        await using var read = store.OpenRead(key);
        await Assert.That(read).IsNotNull();
    }

    [Test]
    public async Task Commit_sameContentTwice_keepsOneBlobAndNoTempFiles()
    {
        var bytes = "%PDF-1.7 same"u8.ToArray();
        var store = CreateStore();
        await using var first = await store.StageAsync(new MemoryStream(bytes), 1024, CancellationToken.None);
        await using var second = await store.StageAsync(new MemoryStream(bytes), 1024, CancellationToken.None);

        var firstKey = await first!.CommitAsync(CancellationToken.None);
        var secondKey = await second!.CommitAsync(CancellationToken.None);

        await Assert.That(secondKey).IsEqualTo(firstKey);
        await Assert.That(Directory.GetFiles(PdfDir).Length).IsEqualTo(1);
        await Assert.That(Directory.GetFiles(TempDir).Length).IsEqualTo(0);
    }

    [Test]
    public async Task Dispose_uncommittedFile_removesTheTempFile()
    {
        var store = CreateStore();
        var staged = await store.StageAsync(new MemoryStream("%PDF-1.7"u8.ToArray()), 1024, CancellationToken.None);

        await staged!.DisposeAsync();

        await Assert.That(Directory.GetFiles(TempDir).Length).IsEqualTo(0);
        await Assert.That(Directory.Exists(PdfDir)).IsFalse();
    }

    [Test]
    public async Task Dispose_committedFile_keepsTheBlob()
    {
        var store = CreateStore();
        var staged = await store.StageAsync(new MemoryStream("%PDF-1.7 keep"u8.ToArray()), 1024, CancellationToken.None);
        await staged!.CommitAsync(CancellationToken.None);

        await staged.DisposeAsync();

        await Assert.That(Directory.GetFiles(PdfDir).Length).IsEqualTo(1);
    }

    [Test]
    [Arguments("pdf/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.pdf\n")]
    [Arguments("pdf/../../secret.pdf")]
    [Arguments("../pdf/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.pdf")]
    [Arguments("pdf/ABCDEF.pdf")]
    [Arguments("tmp/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [Arguments("")]
    public async Task OpenRead_malformedKey_returnsNull(string key)
    {
        var store = CreateStore();

        await Assert.That(store.OpenRead(key)).IsNull();
    }

    [Test]
    public async Task OpenRead_wellFormedKeyOfAMissingFile_returnsNull()
    {
        var store = CreateStore();

        await Assert.That(store.OpenRead($"pdf/{new string('a', 64)}.pdf")).IsNull();
    }

    [Test]
    public async Task DeleteStaleStagedFiles_removesOnlyFilesOlderThanTheAge()
    {
        var store = CreateStore();
        Directory.CreateDirectory(TempDir);
        var stale = Path.Combine(TempDir, "stale");
        var fresh = Path.Combine(TempDir, "fresh");
        await File.WriteAllTextAsync(stale, "x");
        await File.WriteAllTextAsync(fresh, "x");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-2));

        var deleted = store.DeleteStaleStagedFiles(TimeSpan.FromHours(1));

        await Assert.That(deleted).IsEqualTo(1);
        await Assert.That(File.Exists(stale)).IsFalse();
        await Assert.That(File.Exists(fresh)).IsTrue();
    }

    [Test]
    public async Task DeleteStaleStagedFiles_withoutATmpDirectory_returnsZero()
    {
        await Assert.That(CreateStore().DeleteStaleStagedFiles(TimeSpan.FromHours(1))).IsEqualTo(0);
    }

    [Test]
    public async Task Validator_writablePath_createsTheDirectoriesAndSucceeds()
    {
        var result = new StorageOptionsValidator().Validate(null, new StorageOptions { Path = _root });

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(Directory.Exists(PdfDir)).IsTrue();
        await Assert.That(Directory.Exists(TempDir)).IsTrue();
        await Assert.That(Directory.GetFileSystemEntries(TempDir).Length).IsEqualTo(0);
    }

    [Test]
    public async Task Validator_pathIsAFile_failsWithTheReason()
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "not-a-directory");
        await File.WriteAllTextAsync(file, "x");

        var result = new StorageOptionsValidator().Validate(null, new StorageOptions { Path = file });

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(result.FailureMessage).Contains("Documents:Storage:Path");
    }

    [Test]
    public async Task Validator_relativePath_resolvesAgainstTheContentRoot()
    {
        Directory.CreateDirectory(_root);

        var result = new StorageOptionsValidator(new FakeEnvironment(_root)).Validate(null, new StorageOptions { Path = "./store" });

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(Directory.Exists(Path.Combine(_root, "store", "pdf"))).IsTrue();
    }

    [Test]
    public async Task Stage_diskCannotBeWritten_throwsAStoreExceptionNotAnIoException()
    {
        // "tmp" is a file where the store needs a directory: the disk refuses, whatever the request looked like.
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(TempDir, "in the way");
        var store = CreateStore();

        var failure = await Assert.That(async () => await store.StageAsync(new MemoryStream(new byte[10]), 1024, CancellationToken.None))
            .Throws<OriginalFileStoreException>();

        await Assert.That(failure!.InnerException).IsAssignableTo<IOException>();
    }

    [Test]
    public async Task Stage_requestBodyFails_keepsItsOwnExceptionTypeAndLeavesNoTempFile()
    {
        var store = CreateStore();

        var failure = await Assert.That(async () => await store.StageAsync(new FailingStream(), 1024, CancellationToken.None))
            .Throws<IOException>();

        await Assert.That(failure).IsNotTypeOf<OriginalFileStoreException>();
        await Assert.That(Directory.GetFiles(TempDir).Length).IsEqualTo(0);
    }

    private sealed class FailingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException("The client went away.");

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("The client went away.");

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FakeEnvironment(string contentRoot) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";

        public string ApplicationName { get; set; } = "Test";

        public string ContentRootPath { get; set; } = contentRoot;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    /// <summary>Yields one chunk, then cancels the token and blocks on the next read, like a client that disconnects mid-upload.</summary>
    private sealed class CancelAfterFirstReadStream(CancellationTokenSource cts) : Stream
    {
        private bool _first = true;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_first)
            {
                _first = false;
                buffer.Span[..10].Clear();
                await cts.CancelAsync();
                return 10;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
