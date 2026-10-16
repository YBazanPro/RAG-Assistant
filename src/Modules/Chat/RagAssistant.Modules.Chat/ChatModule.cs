using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RagAssistant.Modules.Chat.Answering;
using RagAssistant.Modules.Chat.Options;
using RagAssistant.Modules.Chat.Persistence;
using RagAssistant.SharedKernel.Common.Behaviors;
using RagAssistant.SharedKernel.Common.Extensions;
using RagAssistant.SharedKernel.Persistence;

namespace RagAssistant.Modules.Chat;

public static class ChatModule
{
    public const string ConnectionStringName = "ragassistant";

    /// <summary>Registers the DbContext and its migration target. Also used by the MigrationService, which needs nothing else.</summary>
    public static IServiceCollection AddChatPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is not configured.");

        services.AddDbContext<ChatDbContext>(options => options.UseNpgsql(
            connectionString,
            npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", ChatDbContext.Schema)));
        services.AddMigrationTarget<ChatDbContext>();
        return services;
    }

    /// <summary>The module needs an <c>IChatClient</c> and the Documents <c>IDocumentSearch</c>; the host registers both.</summary>
    public static IServiceCollection AddChatModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddChatPersistence(configuration);

        services.AddOptions<GenerationOptions>()
            .Bind(configuration.GetSection(GenerationOptions.Section))
            .ValidateDataAnnotations()
            .Validate(o => o.IsConsistent, "Chat:Generation: every duration must be greater than zero, and TotalTimeout greater than FirstTokenTimeout.")
            .ValidateOnStart();
        services.AddOptions<ChatRetrievalOptions>()
            .Bind(configuration.GetSection(ChatRetrievalOptions.Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<ConversationOptions>()
            .Bind(configuration.GetSection(ConversationOptions.Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddMediator(options =>
        {
            options.Namespace = "RagAssistant.Modules.Chat.Generated";
            options.ServiceLifetime = ServiceLifetime.Scoped;
            options.GenerateTypesAsInternal = true; // the generated helper types would otherwise clash with other modules and tests
            options.Assemblies = [typeof(ChatModule)]; // the default scans every referenced assembly, i.e. other modules
            options.PipelineBehaviors = [typeof(ValidationBehavior<,>)];
        });
        services.AddValidatorsFromAssembly(typeof(ChatModule).Assembly, includeInternalTypes: true);
        services.AddEndpoints(typeof(ChatModule).Assembly);

        services.AddScoped<AnswerPipeline>();
        return services;
    }
}
