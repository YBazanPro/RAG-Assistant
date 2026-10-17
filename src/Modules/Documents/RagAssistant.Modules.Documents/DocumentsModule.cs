using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RagAssistant.Modules.Documents.Common;
using RagAssistant.Modules.Documents.Ingestion;
using RagAssistant.Modules.Documents.Ingestion.Extraction;
using RagAssistant.Modules.Documents.Options;
using RagAssistant.Modules.Documents.Pdf;
using RagAssistant.Modules.Documents.Persistence;
using RagAssistant.Modules.Documents.Retrieval;
using RagAssistant.Modules.Documents.Storage;
using RagAssistant.SharedKernel.Common.Behaviors;
using RagAssistant.SharedKernel.Common.Extensions;
using RagAssistant.SharedKernel.Contracts.Documents;
using RagAssistant.SharedKernel.Persistence;

namespace RagAssistant.Modules.Documents;

public static class DocumentsModule
{
    public const string ConnectionStringName = "ragassistant";

    /// <summary>Registers the DbContext and its migration target. Also used by the MigrationService, which needs nothing else.</summary>
    public static IServiceCollection AddDocumentsPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is not configured.");

        services.AddDbContext<DocumentsDbContext>(options => options.UseNpgsql(connectionString, npgsql =>
        {
            npgsql.UseVector();
            npgsql.MigrationsHistoryTable("__EFMigrationsHistory", DocumentsDbContext.Schema);
        }));
        services.AddMigrationTarget<DocumentsDbContext>();
        return services;
    }

    public static IServiceCollection AddDocumentsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDocumentsPersistence(configuration);

        services.AddOptions<ImportOptions>()
            .Bind(configuration.GetSection(ImportOptions.Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<StorageOptions>()
            .Bind(configuration.GetSection(StorageOptions.Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<StorageOptions>, StorageOptionsValidator>();
        services.AddSingleton<IOriginalFileStore, FileSystemOriginalFileStore>();
        services.AddSingleton<IPdfProbe, PdfProbe>();
        services.AddSingleton<IPdfTextExtractor, PdfTextExtractor>();
        services.AddHostedService<StagedFileSweeper>();
        services.AddOptions<PdfOptions>()
            .Bind(configuration.GetSection(PdfOptions.Section))
            .ValidateDataAnnotations()
            .Validate(o => o.HasPositiveDurations, "Documents:Pdf: every timeout must be greater than zero.")
            .ValidateOnStart();
        services.AddOptions<EmbeddingOptions>()
            .Bind(configuration.GetSection(EmbeddingOptions.Section))
            .ValidateDataAnnotations()
            .Validate(
                o => o.Dimensions == DocumentsDbContext.EmbeddingDimensions,
                $"Documents:Embeddings:Dimensions must be {DocumentsDbContext.EmbeddingDimensions}, the width of the passages.embedding column.")
            .ValidateOnStart();

        services.AddOptions<ChunkingOptions>()
            .Bind(configuration.GetSection(ChunkingOptions.Section))
            .ValidateDataAnnotations()
            .Validate(o => o.IsConsistent, "Documents:Chunking: OverlapTokens and MinTokens must be below MaxTokens.")
            .ValidateOnStart();
        services.AddOptions<IngestionOptions>()
            .Bind(configuration.GetSection(IngestionOptions.Section))
            .ValidateDataAnnotations()
            .Validate(o => o.HasPositiveDurations, "Documents:Ingestion: every interval, delay and timeout must be greater than zero.")
            .ValidateOnStart();

        services.AddOptions<RetrievalOptions>()
            .Bind(configuration.GetSection(RetrievalOptions.Section))
            .ValidateDataAnnotations()
            .Validate(
                o => o.IsConsistent,
                "Documents:Retrieval: DefaultTopK must not exceed MaxTopK, MaxCandidates must be at least MaxTopK, "
                + "ConfidentScore must be at least MinScore, and both durations must be greater than zero.")
            .ValidateOnStart();

        services.AddOptions<DocumentsAdminOptions>()
            .Bind(configuration.GetSection(DocumentsAdminOptions.Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddMediator(options =>
        {
            options.Namespace = "RagAssistant.Modules.Documents.Generated";
            options.ServiceLifetime = ServiceLifetime.Scoped;
            options.GenerateTypesAsInternal = true; // the generated helper types would otherwise clash with other modules and tests
            options.Assemblies = [typeof(DocumentsModule)]; // the default scans every referenced assembly, i.e. other modules
            options.PipelineBehaviors = [typeof(ValidationBehavior<,>)];
        });
        services.AddValidatorsFromAssembly(typeof(DocumentsModule).Assembly, includeInternalTypes: true);
        services.AddEndpoints(typeof(DocumentsModule).Assembly);
        services.AddSingleton<DocumentsMetrics>();

        services.AddSingleton<IngestionQueue>();
        services.AddSingleton<ISourceExtractor, MarkdownSourceExtractor>();
        services.AddSingleton<ISourceExtractor, PlainTextSourceExtractor>();
        services.AddSingleton<ISourceExtractor, PdfSourceExtractor>();
        services.AddScoped<PassageEmbedder>();
        services.AddScoped<IngestionRunProcessor>();
        services.AddHostedService<IngestionWorker>();

        // Retrieval (spec 011); HybridCache itself is registered by the host (ADR 0008).
        services.AddScoped<QueryEmbedder>();
        services.AddScoped<PassageCandidates>();
        services.AddScoped<IDocumentSearch, DocumentSearch>();
        services.AddScoped<IDocumentCatalog, DocumentCatalog>();
        services.AddScoped<IDocumentTitles, DocumentTitles>();
        return services;
    }
}
