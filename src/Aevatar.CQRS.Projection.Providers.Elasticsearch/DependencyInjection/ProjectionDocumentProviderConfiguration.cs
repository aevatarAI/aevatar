using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;

// Refactor (iter86/cluster-086-projection-provider-config-duplication):
//   Old pattern: each capability SCE parsed Projection:Document:Providers:* and
//   rebound ElasticsearchProjectionDocumentStoreOptions with local policy drift.
//   New principle: shared provider selection and ES typed binding live beside the
//   provider helper; capability SCEs only register their readmodel document types.
public static class ProjectionDocumentProviderConfiguration
{
    public const string InMemoryEnabledPath = "Projection:Document:Providers:InMemory:Enabled";
    public const string DenyInMemoryDocumentReadStorePath = "Projection:Policies:DenyInMemoryDocumentReadStore";
    public const string PolicyEnvironmentPath = "Projection:Policies:Environment";

    public static ProjectionDocumentProviderSelection Resolve(
        IConfiguration? configuration,
        string capabilityName,
        ILogger? logger = null)
    {
        var elasticsearchEnabled = ElasticsearchProjectionConfiguration.IsEnabled(
            configuration,
            logger,
            capabilityName);
        var inMemoryEnabled = ResolveOptionalBool(
            configuration?[InMemoryEnabledPath],
            fallbackValue: !elasticsearchEnabled);
        var providerCount = (elasticsearchEnabled ? 1 : 0) + (inMemoryEnabled ? 1 : 0);
        if (providerCount != 1)
        {
            throw new InvalidOperationException(
                $"Exactly one document projection provider must be enabled for {capabilityName}. " +
                "Configure either Projection:Document:Providers:Elasticsearch:Enabled=true or " +
                "Projection:Document:Providers:InMemory:Enabled=true.");
        }

        EnforceInMemoryPolicy(configuration, inMemoryEnabled);

        return new ProjectionDocumentProviderSelection(
            elasticsearchEnabled
                ? ProjectionDocumentProviderKind.Elasticsearch
                : ProjectionDocumentProviderKind.InMemory,
            elasticsearchEnabled,
            inMemoryEnabled);
    }

    public static ElasticsearchProjectionDocumentStoreOptions BindRequiredElasticsearchOptions(
        IConfiguration configuration)
    {
        var options = ElasticsearchProjectionConfiguration.BindOptions(configuration);
        if (options.Endpoints.Count == 0)
        {
            throw new InvalidOperationException(
                $"{ElasticsearchProjectionConfiguration.SectionPath} is enabled but Endpoints is empty.");
        }

        return options;
    }

    public static bool ResolveOptionalBool(string? rawValue, bool fallbackValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            return fallbackValue;
        if (!bool.TryParse(rawValue, out var parsed))
            throw new InvalidOperationException($"Invalid boolean value '{rawValue}'.");

        return parsed;
    }

    private static void EnforceInMemoryPolicy(
        IConfiguration? configuration,
        bool inMemoryEnabled)
    {
        if (!inMemoryEnabled || configuration is null)
            return;

        var denyInMemory = ResolveOptionalBool(
            configuration[DenyInMemoryDocumentReadStorePath],
            fallbackValue: false);
        var environment = ResolveRuntimeEnvironment(configuration[PolicyEnvironmentPath]);
        if (denyInMemory || IsProductionEnvironment(environment))
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
        return aspnetEnvironment?.Trim() ?? string.Empty;
    }

    private static bool IsProductionEnvironment(string environment) =>
        string.Equals(environment, "Production", StringComparison.OrdinalIgnoreCase);
}

public readonly record struct ProjectionDocumentProviderSelection(
    ProjectionDocumentProviderKind Kind,
    bool ElasticsearchEnabled,
    bool InMemoryEnabled);

public enum ProjectionDocumentProviderKind
{
    InMemory,
    Elasticsearch,
}
