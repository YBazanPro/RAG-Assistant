using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RagAssistant.Modules.Evaluation.Persistence;
using RagAssistant.SharedKernel.Common.Behaviors;
using RagAssistant.SharedKernel.Common.Extensions;
using RagAssistant.SharedKernel.Persistence;

namespace RagAssistant.Modules.Evaluation;

/// <summary>Evaluation runs uploaded by the harness (spec 020). No contract with other modules: it only stores and compares reports.</summary>
public static class EvaluationModule
{
    public const string ConnectionStringName = "ragassistant";

    /// <summary>Registers the DbContext and its migration target. Also used by the MigrationService, which needs nothing else.</summary>
    public static IServiceCollection AddEvaluationPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is not configured.");

        services.AddDbContext<EvaluationDbContext>(options => options.UseNpgsql(
            connectionString,
            npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", EvaluationDbContext.Schema)));
        services.AddMigrationTarget<EvaluationDbContext>();
        return services;
    }

    public static IServiceCollection AddEvaluationModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddEvaluationPersistence(configuration);

        services.AddMediator(options =>
        {
            options.Namespace = "RagAssistant.Modules.Evaluation.Generated";
            options.ServiceLifetime = ServiceLifetime.Scoped;
            options.GenerateTypesAsInternal = true; // the generated helper types would otherwise clash with other modules and tests
            options.Assemblies = [typeof(EvaluationModule)]; // the default scans every referenced assembly, i.e. other modules
            options.PipelineBehaviors = [typeof(ValidationBehavior<,>)];
        });
        services.AddValidatorsFromAssembly(typeof(EvaluationModule).Assembly, includeInternalTypes: true);
        services.AddEndpoints(typeof(EvaluationModule).Assembly);
        return services;
    }
}
