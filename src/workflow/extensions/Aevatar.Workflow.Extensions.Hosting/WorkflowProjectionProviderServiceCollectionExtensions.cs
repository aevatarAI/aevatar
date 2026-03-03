using Aevatar.CQRS.Projection.Abstractions;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.Workflow.Projection.ReadModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Extensions.Hosting;

public static class WorkflowProjectionProviderServiceCollectionExtensions
{
    public static IServiceCollection AddWorkflowProjectionReadModelProviders(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (services.Any(x => x.ServiceType == typeof(WorkflowProjectionProviderRegistrationsMarker)))
            return services;

        EnsureLegacyProviderOptionsNotUsed(configuration);

        services.AddSingleton<WorkflowProjectionProviderRegistrationsMarker>();

        var enableElasticsearchDocument = ResolveElasticsearchDocumentEnabled(configuration);
        var enableInMemoryDocument = ResolveOptionalBool(
            configuration["Projection:Document:Providers:InMemory:Enabled"],
            fallbackValue: !enableElasticsearchDocument);

        EnforceDocumentProviderPolicy(configuration, enableInMemoryDocument);

        var documentProviderCount = (enableElasticsearchDocument ? 1 : 0) + (enableInMemoryDocument ? 1 : 0);
        if (documentProviderCount != 1)
        {
            throw new InvalidOperationException(
                "Exactly one document projection provider must be enabled. Configure either Projection:Document:Providers:Elasticsearch:Enabled=true or Projection:Document:Providers:InMemory:Enabled=true.");
        }

        if (enableElasticsearchDocument)
        {
            services.AddElasticsearchDocumentProjectionStore<WorkflowExecutionReport, string>(
                optionsFactory: _ => BuildElasticsearchDocumentOptions(configuration),
                metadataFactory: sp =>
                {
                    var metadataProvider = sp.GetRequiredService<IProjectionDocumentMetadataProvider<WorkflowExecutionReport>>();
                    return metadataProvider.Metadata;
                },
                keySelector: report => report.RootActorId,
                keyFormatter: key => key);
        }
        else
        {
            services.AddInMemoryDocumentProjectionStore<WorkflowExecutionReport, string>(
                keySelector: report => report.RootActorId,
                keyFormatter: key => key,
                listSortSelector: report => report.CreatedAt,
                listTakeMax: 200);
        }

        return services;
    }

    private static void EnsureLegacyProviderOptionsNotUsed(IConfiguration configuration)
    {
        var legacyDocumentProvider = configuration["Projection:Document:Provider"]?.Trim();

        if (legacyDocumentProvider?.Length > 0)
        {
            throw new InvalidOperationException(
                "Legacy provider single-selection options are no longer supported. " +
                "Use Projection:Document:Providers:*:Enabled with exactly one provider enabled.");
        }
    }

    private static bool ResolveElasticsearchDocumentEnabled(IConfiguration configuration)
    {
        var section = configuration.GetSection("Projection:Document:Providers:Elasticsearch");
        var explicitEnabled = section["Enabled"];
        var hasEndpoints = section
            .GetSection("Endpoints")
            .GetChildren()
            .Select(x => x.Value?.Trim() ?? "")
            .Any(x => x.Length > 0);

        return ResolveOptionalBool(explicitEnabled, hasEndpoints);
    }

    private static ElasticsearchProjectionDocumentStoreOptions BuildElasticsearchDocumentOptions(
        IConfiguration configuration)
    {
        var options = new ElasticsearchProjectionDocumentStoreOptions();
        configuration.GetSection("Projection:Document:Providers:Elasticsearch").Bind(options);
        if (options.Endpoints.Count == 0)
        {
            throw new InvalidOperationException(
                "Projection:Document:Providers:Elasticsearch is enabled but Endpoints is empty.");
        }

        return options;
    }

    private static void EnforceDocumentProviderPolicy(
        IConfiguration configuration,
        bool enableInMemoryDocumentProvider)
    {
        var denyInMemoryDocumentProvider = ResolveOptionalBool(
            configuration["Projection:Policies:DenyInMemoryDocumentReadStore"],
            fallbackValue: false);
        var environment = ResolveRuntimeEnvironment(configuration["Projection:Policies:Environment"]);
        var production = IsProductionEnvironment(environment);

        if ((denyInMemoryDocumentProvider || production) && enableInMemoryDocumentProvider)
        {
            throw new InvalidOperationException(
                "InMemory document provider is not allowed by projection policy. " +
                "Disable Projection:Document:Providers:InMemory:Enabled and configure Elasticsearch.");
        }
    }

    private static string ResolveRuntimeEnvironment(string? configuredEnvironment)
    {
        if (!string.IsNullOrWhiteSpace(configuredEnvironment))
            return configuredEnvironment.Trim();

        var dotnetEnvironment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        if (!string.IsNullOrWhiteSpace(dotnetEnvironment))
            return dotnetEnvironment.Trim();

        var aspnetEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        return aspnetEnvironment?.Trim() ?? "";
    }

    private static bool IsProductionEnvironment(string environment)
    {
        return string.Equals(environment, "Production", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ResolveOptionalBool(string? rawValue, bool fallbackValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            return fallbackValue;

        if (!bool.TryParse(rawValue, out var parsed))
            throw new InvalidOperationException($"Invalid boolean value '{rawValue}'.");

        return parsed;
    }

    private sealed class WorkflowProjectionProviderRegistrationsMarker;
}
