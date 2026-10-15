using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using RagAssistant.Integration.Tests.Documents;
using RagAssistant.SharedKernel.Persistence;

namespace RagAssistant.Integration.Tests.Fixtures;

public sealed record ImportedDocument(Guid DocumentId, Guid VersionId, Guid RunId);

/// <summary>
/// A whole application on its own database with a scriptable embedding fake and fast intervals. Each ingestion test builds one,
/// so a worker never picks up another test's runs; a second harness on the same database name models a restart.
/// </summary>
public sealed class IngestionHarness : WebApplicationFactory<Program>
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    private static readonly Dictionary<string, string> DefaultSettings = new()
    {
        ["Documents:Ingestion:ProbeInterval"] = "00:00:00.050",
        ["Documents:Ingestion:ProgressPollInterval"] = "00:00:00.050",
        ["Documents:Ingestion:EmbeddingRetryBaseDelay"] = "00:00:00.001",
        ["Documents:Ingestion:EmbeddingTimeout"] = "00:00:05",
        // Tiny test documents must not be merged into each other; the chunker has its own unit tests.
        ["Documents:Chunking:MinTokens"] = "1",
    };

    private const string StorageKey = "Documents:Storage:Path";

    private readonly string _ownStoragePath = Path.Combine(Path.GetTempPath(), "ragassistant-tests", Guid.NewGuid().ToString("N"));
    private readonly string _connectionString;
    private readonly Dictionary<string, string> _settings;

    private IngestionHarness(
        string connectionString, string databaseName, FakeEmbeddingGenerator generator, Dictionary<string, string> settings, ManualTimeProvider? clock)
    {
        Clock = clock;
        _connectionString = connectionString;
        DatabaseName = databaseName;
        Generator = generator;
        _settings = settings;
    }

    public string DatabaseName { get; }

    /// <summary>The original-file store of this host; a restart harness passes the same path to find the files again.</summary>
    public string StoragePath => _settings.TryGetValue(StorageKey, out var path) ? path : _ownStoragePath;

    /// <summary>Set when the test controls time; the application then only moves when the test advances it.</summary>
    public ManualTimeProvider? Clock { get; }

    public FakeEmbeddingGenerator Generator { get; }

    public CapturingLoggerProvider Logs { get; } = new();

    public NpgsqlDataSource Db => Services.GetRequiredService<NpgsqlDataSource>();

    /// <param name="databaseName">Reuse a name to start a second host (a "restart") on the same data.</param>
    public static async Task<IngestionHarness> StartAsync(
        PostgresFixture postgres,
        string? databaseName = null,
        FakeEmbeddingGenerator? generator = null,
        IReadOnlyDictionary<string, string>? settings = null,
        ManualTimeProvider? clock = null)
    {
        databaseName ??= $"ing_{Guid.NewGuid():N}";
        var connection = new NpgsqlConnectionStringBuilder(postgres.ConnectionString)
        {
            Database = databaseName,
            // Every harness has its own pools; keep them small and short-lived so a test run stays under max_connections.
            MaxPoolSize = 4,
            ConnectionIdleLifetime = 1,
            ConnectionPruningInterval = 1,
        }.ConnectionString;

        await using (var admin = new NpgsqlConnection(postgres.ConnectionString))
        {
            await admin.OpenAsync();
            await using var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = $1", admin) { Parameters = { new() { Value = databaseName } } };
            if (await exists.ExecuteScalarAsync() is null)
            {
                await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin);
                await create.ExecuteNonQueryAsync();
            }
        }

        var merged = new Dictionary<string, string>(DefaultSettings);
        foreach (var (key, value) in settings ?? new Dictionary<string, string>())
        {
            merged[key] = value;
        }

        var harness = new IngestionHarness(connection, databaseName, generator ?? new FakeEmbeddingGenerator(), merged, clock);
        await using var scope = harness.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DatabaseMigrator>().MigrateAsync(CancellationToken.None);
        return harness;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_ownStoragePath))
        {
            try
            {
                Directory.Delete(_ownStoragePath, recursive: true);
            }
            catch (IOException)
            {
                // Best effort: a leftover temp folder is harmless.
            }
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:ragassistant", _connectionString);
        builder.UseSetting(StorageKey, StoragePath);
        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
        builder.ConfigureServices(services =>
        {
            services.AddDatabaseMigrator();
            services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(Generator);
            if (Clock is not null)
            {
                services.AddSingleton<TimeProvider>(Clock);
            }

            TestAuthentication.Configure(services);
        });
    }

    public static string UniqueMarkdown(string body = "Some policy text that is long enough to index.") =>
        $"# Policy {Guid.NewGuid()}\n\n## Scope\n\n{body}\n\n## Rules\n\nSecond section text.\n";

    public async Task<ImportedDocument> ImportAsync(string? markdown = null, string fileName = "policy.md")
    {
        using var client = this.CreateClientAs(Persona.ContentOwner);
        using var form = DocumentUploads.Form(fileName, Encoding.UTF8.GetBytes(markdown ?? UniqueMarkdown()), DocumentUploads.UniqueTitle());
        var response = await client.PostAsync("/api/documents", form);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new(body.GetProperty("documentId").GetGuid(), body.GetProperty("versionId").GetGuid(), body.GetProperty("ingestionRunId").GetGuid());
    }

    public async Task ExecuteAsync(string sql, params object[] parameters)
    {
        await using var command = Command(sql, parameters);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<List<object?[]>> RowsAsync(string sql, params object[] parameters)
    {
        await using var command = Command(sql, parameters);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<object?[]>();
        while (await reader.ReadAsync())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = await reader.IsDBNullAsync(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    public Task<T?> ScalarAsync<T>(string sql, params object[] parameters) => DocumentUploads.ScalarAsync<T>(Db, sql, parameters);

    public Task<string?> RunStatusAsync(Guid runId) =>
        ScalarAsync<string>("SELECT status FROM documents.ingestion_runs WHERE id = $1", runId);

    public async Task WaitForRunAsync(Guid runId, string status)
    {
        await WaitUntilAsync(async () => await RunStatusAsync(runId) == status, $"run {runId} to reach {status}, last status {await RunStatusAsync(runId)}");
    }

    /// <summary>Moves the controlled clock forward in steps until the condition holds, giving the application a moment to react after each step.</summary>
    public async Task AdvanceClockUntilAsync(Func<Task<bool>> condition, TimeSpan step, string description)
    {
        var clock = Clock ?? throw new InvalidOperationException("This harness does not control time.");
        using var timeout = new CancellationTokenSource(WaitTimeout);
        while (!await condition())
        {
            if (timeout.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out waiting for {description} (clock advanced {clock.Elapsed}).");
            }

            clock.Advance(step);
            await Task.Delay(20);
        }
    }

    public static async Task WaitUntilAsync(Func<Task<bool>> condition, string description)
    {
        using var timeout = new CancellationTokenSource(WaitTimeout);
        while (!await condition())
        {
            if (timeout.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out waiting for {description}.");
            }

            await Task.Delay(25);
        }
    }

    /// <summary>"Stage/Status" for every event of the run, in id order.</summary>
    public async Task<List<string>> EventsAsync(Guid runId) =>
        [.. (await RowsAsync("SELECT stage, status FROM documents.ingestion_events WHERE run_id = $1 ORDER BY id", runId)).Select(r => $"{r[0]}/{r[1]}")];

    public async Task<List<long>> EventIdsAsync(Guid runId) =>
        [.. (await RowsAsync("SELECT id FROM documents.ingestion_events WHERE run_id = $1 ORDER BY id", runId)).Select(r => (long)r[0]!)];

    /// <summary>Adds a queued version 2 to an existing document, the way "upload a new version" (1.7) will.</summary>
    public async Task<Guid> AddQueuedVersionAsync(Guid documentId, int number, string markdown)
    {
        var versionId = Guid.CreateVersion7();
        var runId = Guid.CreateVersion7();
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(markdown)));
        await ExecuteAsync(
            """
            INSERT INTO documents.document_versions
                (id, document_id, number, origin, status, source_format, original_file_name, content, content_hash, size_bytes, created_at, created_by)
            VALUES ($1, $2, $3, 'Upload', 'Queued', 'Markdown', 'v.md', $4, $5, $6, now(), 'test')
            """,
            versionId, documentId, number, markdown, hash, Encoding.UTF8.GetByteCount(markdown));
        await ExecuteAsync(
            "INSERT INTO documents.ingestion_runs (id, version_id, attempt, status, current_stage, queued_at) VALUES ($1, $2, 1, 'Queued', 'Queued', now())",
            runId, versionId);
        await ExecuteAsync(
            "INSERT INTO documents.ingestion_events (run_id, stage, status, message, occurred_at) VALUES ($1, 'Queued', 'Completed', 'Import accepted.', now())",
            runId);
        return runId;
    }

    private NpgsqlCommand Command(string sql, object[] parameters)
    {
        var command = Db.CreateCommand(sql);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }

        return command;
    }
}
