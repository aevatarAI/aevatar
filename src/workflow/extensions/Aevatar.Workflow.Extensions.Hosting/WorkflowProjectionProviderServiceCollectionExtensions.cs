using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Providers.Neo4j.Configuration;
using Aevatar.CQRS.Projection.Providers.Neo4j.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Projection.Runtime;
using Aevatar.Workflow.Projection.ReadModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Workflow.Extensions.Hosting;

public static class WorkflowProjectionProviderServiceCollectionExtensions
{
    // Engine labels surfaced by the read-model inventory (GET /api/cqrs/readmodels). The workflow
    // document store/engine choice is a branch in this file (ES vs InMemory), so the inventory
    // descriptors are declared right next to the store registrations that pick the engine.
    private const string ElasticsearchEngineLabel = "Elasticsearch";
    private const string InMemoryEngineLabel = "dev/InMemory";

    public static IServiceCollection AddWorkflowProjectionReadModelProviders(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        EnsureLegacyProviderOptionsNotUsed(configuration);

        var documentProvider = ProjectionDocumentProviderConfiguration.Resolve(configuration, "Workflow");
        var enableNeo4jGraph = ResolveNeo4jGraphEnabled(configuration);
        var enableInMemoryGraph = ResolveOptionalBool(
            configuration["Projection:Graph:Providers:InMemory:Enabled"],
            fallbackValue: !enableNeo4jGraph);

        EnforceGraphProviderPolicy(configuration, enableInMemoryGraph);

        var graphProviderCount = (enableNeo4jGraph ? 1 : 0) + (enableInMemoryGraph ? 1 : 0);
        if (graphProviderCount > 1)
        {
            throw new InvalidOperationException(
                "Only one graph projection provider can be enabled. Configure either Projection:Graph:Providers:Neo4j:Enabled=true or Projection:Graph:Providers:InMemory:Enabled=true.");
        }

        if (documentProvider.ElasticsearchEnabled)
        {
            AddElasticsearchDocumentStores(services, configuration);
        }
        else
        {
            AddInMemoryDocumentStores(services);
        }

        var expectedGraphProvider = enableNeo4jGraph
            ? new ProjectionGraphProviderStatus("Neo4j", Enabled: true)
            : enableInMemoryGraph
                ? new ProjectionGraphProviderStatus("InMemory", Enabled: true)
                : new ProjectionGraphProviderStatus("Disabled", Enabled: false);
        if (UseExistingGraphProviderOrThrow(services, expectedGraphProvider))
            return services;

        if (enableNeo4jGraph)
        {
            services.AddNeo4jGraphProjectionStore(
                optionsFactory: _ => BuildNeo4jGraphOptions(configuration));
        }
        else if (enableInMemoryGraph)
        {
            services.AddInMemoryGraphProjectionStore();
        }
        else
        {
            services.AddDisabledGraphProjectionStore();
        }

        return services;
    }

    private static void AddElasticsearchDocumentStores(
        IServiceCollection services,
        IConfiguration configuration)
    {
        TryAddElasticsearchDocumentStore<WorkflowExecutionCurrentStateDocument>(
            services,
            configuration,
            static document => document.RootActorId);
        TryAddElasticsearchDocumentStore<WorkflowRunInsightReportDocument>(
            services,
            configuration,
            static report => report.RootActorId);
        TryAddElasticsearchDocumentStore<WorkflowActorBindingDocument>(
            services,
            configuration,
            static document => document.Id);
        TryAddElasticsearchDocumentStore<WorkflowCatalogCurrentStateDocument>(
            services,
            configuration,
            static document => document.Id);
        TryAddElasticsearchDocumentStore<WorkflowExternalApprovalContinuationDocument>(
            services,
            configuration,
            static document => document.Id);
        // The fleet capability authority read model is activated by the workflow projection
        // registration (AddRuntimeFleetCapabilityProjection) and gates every schema adoption;
        // it must have a document store binding on the same provider or admission fails closed.
        TryAddElasticsearchDocumentStore<RuntimeFleetCapabilityAuthorityCurrentStateDocument>(
            services,
            configuration,
            static document => document.Id);

        AddWorkflowReadModelInventoryDescriptors(services, ElasticsearchEngineLabel);
    }

    private static void AddInMemoryDocumentStores(IServiceCollection services)
    {
        TryAddInMemoryDocumentStore<WorkflowExecutionCurrentStateDocument>(
            services,
            static document => document.RootActorId,
            static document => document.UpdatedAt);
        TryAddInMemoryDocumentStore<WorkflowRunInsightReportDocument>(
            services,
            static report => report.RootActorId,
            static report => report.CreatedAt);
        TryAddInMemoryDocumentStore<WorkflowActorBindingDocument>(
            services,
            static document => document.Id,
            static document => document.UpdatedAt);
        TryAddInMemoryDocumentStore<WorkflowCatalogCurrentStateDocument>(
            services,
            static document => document.Id,
            static document => document.UpdatedAt);
        TryAddInMemoryDocumentStore<WorkflowExternalApprovalContinuationDocument>(
            services,
            static document => document.Id,
            static document => document.UpdatedAt);
        TryAddInMemoryDocumentStore<RuntimeFleetCapabilityAuthorityCurrentStateDocument>(
            services,
            static document => document.Id,
            static document => document.UpdatedAt);

        AddWorkflowReadModelInventoryDescriptors(services, InMemoryEngineLabel);
    }

    // Opt-in read-model inventory descriptors, one per workflow document read-model registered above.
    // shape = Document on Elasticsearch, Memory on the InMemory dev store. actorKind is the best-available
    // GAgent/actor kind whose current state each read-model replicates. The graph store is intentionally
    // NOT inventoried here: it exposes no per-read-model document reader, so it cannot cheaply report
    // count/version/updated for a single read-model type (an honest omission, not a faked group).
    private static void AddWorkflowReadModelInventoryDescriptors(IServiceCollection services, string engineLabel)
    {
        var shape = ReferenceEquals(engineLabel, ElasticsearchEngineLabel)
            ? ProjectionReadModelSinkShape.Document
            : ProjectionReadModelSinkShape.Memory;

        TryAddWorkflowReadModelDescriptor<WorkflowExecutionCurrentStateDocument>(services, "workflow-execution-current-state", "WorkflowRunGAgent", engineLabel, shape);
        TryAddWorkflowReadModelDescriptor<WorkflowRunInsightReportDocument>(services, "workflow-run-insight-report", "WorkflowRunGAgent", engineLabel, shape);
        TryAddWorkflowReadModelDescriptor<WorkflowActorBindingDocument>(services, "workflow-actor-binding", "WorkflowDefinitionGAgent", engineLabel, shape);
        TryAddWorkflowReadModelDescriptor<WorkflowCatalogCurrentStateDocument>(services, "workflow-catalog-current-state", "WorkflowDefinitionGAgent", engineLabel, shape);
        TryAddWorkflowReadModelDescriptor<WorkflowExternalApprovalContinuationDocument>(services, "workflow-external-approval-continuation", "WorkflowRunGAgent", engineLabel, shape);
        TryAddWorkflowReadModelDescriptor<RuntimeFleetCapabilityAuthorityCurrentStateDocument>(services, "runtime-fleet-capability-authority-current-state", "RuntimeFleetCapabilityAuthorityGAgent", engineLabel, shape);
    }

    // Registers a single inventory descriptor that delegates to the read-model's already-registered
    // document reader. The closed concrete descriptor type is the idempotence key; TryAddEnumerable
    // cannot be used for the interface factory because factory descriptors have no distinct
    // implementation type and are indistinguishable when more than one read-model is registered.
    private static void TryAddWorkflowReadModelDescriptor<TDocument>(
        IServiceCollection services,
        string name,
        string actorKind,
        string engineLabel,
        ProjectionReadModelSinkShape shape)
        where TDocument : class, IProjectionReadModel<TDocument>, new()
    {
        if (services.Any(static descriptor =>
                descriptor.ServiceType == typeof(ProjectionDocumentReadModelDescriptor<TDocument>)))
            return;

        services.AddSingleton<ProjectionDocumentReadModelDescriptor<TDocument>>(sp =>
            new ProjectionDocumentReadModelDescriptor<TDocument>(
                name,
                shape,
                engineLabel,
                actorKind,
                sp.GetRequiredService<IProjectionDocumentReader<TDocument, string>>()));
        services.AddSingleton<IProjectionReadModelDescriptor>(sp =>
            sp.GetRequiredService<ProjectionDocumentReadModelDescriptor<TDocument>>());
    }

    private static bool HasAnyDocumentReader<TDocument>(IServiceCollection services)
        where TDocument : class, IProjectionReadModel<TDocument>, new()
    {
        return services.Any(x => x.ServiceType == typeof(IProjectionDocumentReader<TDocument, string>));
    }

    private static bool HasDocumentReaderForProvider<TDocument>(
        IServiceCollection services,
        ProjectionDocumentProviderKind providerKind)
        where TDocument : class, IProjectionReadModel<TDocument>, new()
    {
        return providerKind switch
        {
            ProjectionDocumentProviderKind.Elasticsearch => services.Any(x => x.ServiceType == typeof(ElasticsearchProjectionDocumentStore<TDocument, string>)),
            ProjectionDocumentProviderKind.InMemory => services.Any(x => x.ServiceType == typeof(InMemoryProjectionDocumentStore<TDocument, string>)),
            _ => false,
        };
    }

    private static void EnsureCompatibleDocumentReaderProvider<TDocument>(
        IServiceCollection services,
        ProjectionDocumentProviderKind providerKind)
        where TDocument : class, IProjectionReadModel<TDocument>, new()
    {
        if (!HasAnyDocumentReader<TDocument>(services))
            return;
        if (HasDocumentReaderForProvider<TDocument>(services, providerKind))
            return;

        throw new InvalidOperationException(
            $"Projection document reader for {typeof(TDocument).Name} is already registered with a different provider.");
    }

    private static void TryAddElasticsearchDocumentStore<TDocument>(
        IServiceCollection services,
        IConfiguration configuration,
        Func<TDocument, string> keySelector)
        where TDocument : class, IProjectionReadModel<TDocument>, new()
    {
        EnsureCompatibleDocumentReaderProvider<TDocument>(services, ProjectionDocumentProviderKind.Elasticsearch);
        if (HasDocumentReaderForProvider<TDocument>(services, ProjectionDocumentProviderKind.Elasticsearch))
            return;

        services.AddElasticsearchDocumentProjectionStore<TDocument, string>(
            optionsFactory: _ => ProjectionDocumentProviderConfiguration.BindRequiredElasticsearchOptions(configuration),
            metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataProvider<TDocument>>().Metadata,
            keySelector: keySelector,
            keyFormatter: static key => key);
    }

    private static void TryAddInMemoryDocumentStore<TDocument>(
        IServiceCollection services,
        Func<TDocument, string> keySelector,
        Func<TDocument, object?> defaultSortSelector)
        where TDocument : class, IProjectionReadModel<TDocument>, new()
    {
        EnsureCompatibleDocumentReaderProvider<TDocument>(services, ProjectionDocumentProviderKind.InMemory);
        if (HasDocumentReaderForProvider<TDocument>(services, ProjectionDocumentProviderKind.InMemory))
            return;

        services.AddInMemoryDocumentProjectionStore<TDocument, string>(
            keySelector: keySelector,
            keyFormatter: static key => key,
            defaultSortSelector: defaultSortSelector,
            queryTakeMax: 200);
    }

    private static void EnsureLegacyProviderOptionsNotUsed(IConfiguration configuration)
    {
        var legacyDocumentProvider = configuration["Projection:Document:Provider"]?.Trim();
        var legacyGraphProvider = configuration["Projection:Graph:Provider"]?.Trim();

        if (legacyDocumentProvider?.Length > 0 || legacyGraphProvider?.Length > 0)
        {
            throw new InvalidOperationException(
                "Legacy provider single-selection options are no longer supported. " +
                "Use Projection:Document:Providers:*:Enabled with exactly one document provider and " +
                "Projection:Graph:Providers:*:Enabled with at most one graph provider.");
        }
    }

    private static bool ResolveNeo4jGraphEnabled(IConfiguration configuration)
    {
        var section = configuration.GetSection("Projection:Graph:Providers:Neo4j");
        return ResolveOptionalBool(section["Enabled"], fallbackValue: false);
    }

    private static bool UseExistingGraphProviderOrThrow(
        IServiceCollection services,
        ProjectionGraphProviderStatus expectedStatus)
    {
        if (!services.Any(x => x.ServiceType == typeof(IProjectionGraphStore)))
            return false;

        var statusDescriptors = services
            .Where(x => x.ServiceType == typeof(ProjectionGraphProviderStatus))
            .ToArray();
        var registeredStatus = statusDescriptors.Length == 1
            ? statusDescriptors[0].ImplementationInstance as ProjectionGraphProviderStatus
            : null;
        var hasVersionedStore = services.Any(x => x.ServiceType == typeof(IVersionedProjectionGraphStore));
        if (!hasVersionedStore || registeredStatus != expectedStatus)
        {
            throw new InvalidOperationException(
                $"The existing graph projection provider registration is incompatible with the configured " +
                $"'{expectedStatus.ProviderName}' provider. Register IProjectionGraphStore, " +
                "IVersionedProjectionGraphStore, and ProjectionGraphProviderStatus as one matching provider.");
        }

        return true;
    }

    private static Neo4jProjectionGraphStoreOptions BuildNeo4jGraphOptions(IConfiguration configuration)
    {
        var options = new Neo4jProjectionGraphStoreOptions();
        configuration.GetSection("Projection:Graph:Providers:Neo4j").Bind(options);
        if (string.IsNullOrWhiteSpace(options.Uri))
        {
            throw new InvalidOperationException(
                "Projection:Graph:Providers:Neo4j is enabled but Uri is empty.");
        }

        if (string.IsNullOrWhiteSpace(options.Password))
        {
            throw new InvalidOperationException(
                "Projection:Graph:Providers:Neo4j is enabled but Password is empty. " +
                "Inject it via environment variable AEVATAR_Projection__Graph__Providers__Neo4j__Password.");
        }

        return options;
    }

    private static void EnforceGraphProviderPolicy(
        IConfiguration configuration,
        bool enableInMemoryGraphProvider)
    {
        var denyInMemoryGraphProvider = ResolveOptionalBool(
            configuration["Projection:Policies:DenyInMemoryGraphFactStore"],
            fallbackValue: false);
        var environment = ResolveRuntimeEnvironment(configuration["Projection:Policies:Environment"]);
        var production = IsProductionEnvironment(environment);

        if ((denyInMemoryGraphProvider || production) && enableInMemoryGraphProvider)
        {
            throw new InvalidOperationException(
                "InMemory graph provider is not allowed by projection policy. " +
                "Disable Projection:Graph:Providers:InMemory:Enabled and either configure Neo4j or disable graph projection.");
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

}
