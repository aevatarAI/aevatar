using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Application.Bindings;
using Aevatar.GAgentService.Application.Services;
using Aevatar.GAgentService.Application.ScopeGAgents;
using Aevatar.GAgentService.Application.Responses;
using Aevatar.GAgentService.Application.Scripts;
using Aevatar.GAgentService.Application.Schedules;
using Aevatar.GAgentService.Application.Workflows;
using Aevatar.GAgentService.Core.Assemblers;
using Aevatar.GAgentService.Core.Schedules;
using Aevatar.GAgentService.Core.Ports;
using Aevatar.GAgentService.Core.Services;
using Aevatar.GAgentService.Infrastructure.Activation;
using Aevatar.GAgentService.Infrastructure.Adapters;
using Aevatar.GAgentService.Infrastructure.Dispatch;
using Aevatar.GAgentService.Infrastructure.Orchestration;
using Aevatar.GAgentService.Infrastructure.Schedules;
using Aevatar.GAgentService.Hosting.Demo;
using Aevatar.GAgentService.Hosting.Endpoints.Schedules;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Hosting.DependencyInjection;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgentService.Projection.DependencyInjection;
using Aevatar.GAgentService.Projection.ReadModels;
using Aevatar.Capabilities.ExecutionActivity;
using Aevatar.AGUI.Contracts;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.Studio.Projection.ReadModels;
using Aevatar.Scripting.Hosting.DependencyInjection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Infrastructure.DependencyInjection;
using Aevatar.Workflow.Projection.Metadata;
using Aevatar.Workflow.Projection.ReadModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Aevatar.GAgentService.Hosting.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGAgentServiceCapability(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (!services.Any(x => x.ServiceType == typeof(Aevatar.Scripting.Hosting.DependencyInjection.ServiceCollectionExtensions.ScriptCapabilityRegistrationsMarker)))
            services.AddScriptCapability(configuration);

        if (!services.Any(x => x.ServiceType == typeof(WorkflowCapabilityServiceCollectionExtensions.WorkflowCapabilityRegistrationsMarker)))
            services.AddWorkflowCapability(configuration);

        services.AddAevatarAgentKindRegistry(builder => builder.ScanAssemblies(typeof(Aevatar.GAgentService.Core.GAgents.ServiceDefinitionGAgent).Assembly));
        services.AddOptions<GAgentServiceDemoOptions>()
            .Bind(configuration.GetSection("GAgentService:Demo"));
        services.AddGAgentServiceProjection();
        services.AddGAgentServiceProjectionReadModelProviders(configuration);
        services.AddGAgentServiceGovernanceCapability(configuration);
        services.TryAddSingleton<PreparedServiceRevisionArtifactAssembler>();
        services.TryAddSingleton<ServiceInvokeReadinessEvaluator>();
        services.TryAddSingleton<IServiceServingTargetResolver, DefaultServiceServingTargetResolver>();
        services.TryAddSingleton<IServiceCommandTargetProvisioner, DefaultServiceCommandTargetProvisioner>();
        services.TryAddSingleton<IServiceRuntimeActivator, DefaultServiceRuntimeActivator>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICommittedStatePublicationHook, ScriptingServiceRevisionRepublishHook>());
        services.TryAddSingleton<IServiceRunRegistrationPort, ServiceRunRegistrationAdapter>();
        services.TryAddSingleton<ILlmSessionRegistrationPort, LlmSessionRegistrationAdapter>();
        services.TryAddSingleton<IResponsesAgentToolStateCommandPort, ResponsesAgentToolStateCommandAdapter>();
        services.TryAddSingleton<ILlmSessionRunObservationService, LlmSessionRunObservationService>();
        services.TryAddSingleton<ILlmRunCore, LlmRunCore>();
        services.TryAddSingleton<ILlmRunExecutionTargetProvisioner, LlmRunExecutionTargetProvisioner>();
        services.TryAddSingleton<LlmRunExecutor>();
        services.TryAddSingleton<ILlmRunExecutor>(sp => sp.GetRequiredService<LlmRunExecutor>());
        services.TryAddSingleton<ILlmRunExecutionService>(sp => sp.GetRequiredService<LlmRunExecutor>());
        services.TryAddSingleton<LlmRunExecutionScheduler>();
        services.TryAddSingleton<ILlmRunExecutionScheduler>(sp => sp.GetRequiredService<LlmRunExecutionScheduler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICommittedStatePublicationHook, LlmRunExecutionReadyHook>());
        services.TryAddSingleton<IResponsesToolClassificationService, ResponsesToolClassificationService>();
        services.AddToolSetRegistry();
        services.TryAddSingleton<IResponsesDirectToolPlanService, ResponsesDirectToolPlanService>();
        services.TryAddSingleton<IServiceInvocationDispatcher, DefaultServiceInvocationDispatcher>();
        services.TryAddSingleton<IExecutionActivityScopeResolver, ExecutionActivityScopeResolver>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Aevatar.Foundation.Abstractions.Hooks.IGAgentExecutionHook, ExecutionActivityPublisherHook>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IServiceImplementationAdapter, StaticServiceImplementationAdapter>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IServiceImplementationAdapter, ScriptingServiceImplementationAdapter>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IServiceImplementationAdapter, WorkflowServiceImplementationAdapter>());
        services.TryAddSingleton<ServiceInvocationResolutionService>();
        services.TryAddSingleton<ServiceInvokeReadinessErrorMapper>();
        services.TryAddSingleton<IServiceCommandPort, ServiceCommandApplicationService>();
        services.TryAddSingleton<IServiceLifecycleQueryPort, ServiceLifecycleQueryApplicationService>();
        services.TryAddSingleton<IServiceServingQueryPort, ServiceServingQueryApplicationService>();
        services.TryAddSingleton<IServiceInvocationPort, ServiceInvocationApplicationService>();
        services.TryAddSingleton<IScheduledServiceInvocationDispatchPort, ScheduledServiceInvocationDispatchPort>();
        services.AddScheduledCredentialExchangePort();
        services.TryAddSingleton<IScheduledDispatchTargetPreparationService, ScheduledDispatchTargetPreparationService>();
        services.TryAddSingleton<IScheduledDispatchApplicationService, ScheduledDispatchApplicationService>();
        services.TryAddSingleton<IScheduledDispatchActorPort, ScheduledDispatchActorPort>();
        services.TryAddTransient<ScheduledDispatchGAgent>();
        services.TryAddSingleton<IStaticGAgentStreamInvocationPort<AGUIEvent>, StaticGAgentStreamInvocationApplicationService>();
        services.AddScopeGAgentDraftRunInteraction();
        services.AddScriptServiceRunInteraction();
        services.AddOptions<ScopeWorkflowCapabilityOptions>()
            .Bind(configuration.GetSection(ScopeWorkflowCapabilityOptions.SectionName));
        services.TryAddSingleton<ScopeWorkflowQueryApplicationService>();
        services.TryAddSingleton<IScopeWorkflowQueryPort>(sp => sp.GetRequiredService<ScopeWorkflowQueryApplicationService>());
        services.TryAddSingleton<IScopeWorkflowCommandPort, ScopeWorkflowCommandApplicationService>();
        services.TryAddSingleton<ISkillWorkflowMountPort, SkillWorkflowMountAdapter>();
        services.TryAddSingleton<IScopeBindingCommandPort, ScopeBindingCommandApplicationService>();
        services.TryAddSingleton<IScopeBindingReadinessQueryPort, ScopeBindingReadinessQueryService>();
        services.TryAddSingleton<IMemberPublishedServiceResolver, DefaultMemberPublishedServiceResolver>();
        services.AddOptions<ScopeScriptCapabilityOptions>()
            .Bind(configuration.GetSection(ScopeScriptCapabilityOptions.SectionName));
        services.TryAddSingleton<ScopeScriptQueryApplicationService>();
        services.TryAddSingleton<IScopeScriptQueryPort>(sp => sp.GetRequiredService<ScopeScriptQueryApplicationService>());
        services.TryAddSingleton<IScopeScriptCommandPort, ScopeScriptCommandApplicationService>();
        services.TryAddSingleton<IScopeScriptSaveObservationPort, ScopeScriptSaveObservationService>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, GAgentServiceDemoBootstrapHostedService>());
        return services;
    }

    public static IServiceCollection AddScheduledDispatchCapability(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddAevatarAgentKindRegistry(builder => builder.Register<ScheduledDispatchGAgent>());
        services.AddGAgentServiceProjection();
        services.AddGAgentServiceProjectionReadModelProviders(configuration);
        services.TryAddSingleton<PreparedServiceRevisionArtifactAssembler>();
        services.TryAddSingleton<IServiceServingTargetResolver, DefaultServiceServingTargetResolver>();
        services.TryAddSingleton<IServiceRunRegistrationPort, ServiceRunRegistrationAdapter>();
        services.TryAddSingleton<ServiceInvocationResolutionService>();
        services.TryAddSingleton<ServiceInvokeReadinessErrorMapper>();
        services.TryAddSingleton<IInvokeAdmissionAuthorizer, ScheduledDispatchInvokeAdmissionAuthorizer>();
        services.TryAddSingleton<IServiceInvocationDispatcher, DefaultServiceInvocationDispatcher>();
        services.TryAddSingleton<IServiceInvocationPort, ServiceInvocationApplicationService>();
        services.TryAddSingleton<IScheduledServiceInvocationDispatchPort, ScheduledServiceInvocationDispatchPort>();
        services.AddScheduledCredentialExchangePort();
        services.TryAddSingleton<IScheduledDispatchTargetPreparationService, ScheduledDispatchTargetPreparationService>();
        services.TryAddSingleton<IScheduledDispatchApplicationService, ScheduledDispatchApplicationService>();
        services.TryAddSingleton<IScheduledDispatchActorPort, ScheduledDispatchActorPort>();
        services.TryAddTransient<ScheduledDispatchGAgent>();
        return services;
    }

    private static void AddScheduledCredentialExchangePort(this IServiceCollection services) =>
        services.TryAddSingleton<IScheduledServiceInvocationCredentialExchangePort>(sp =>
            sp.GetService<INyxIdCapabilityBroker>() is { } broker
                ? new NyxIdScheduledServiceInvocationCredentialExchangePort(
                    broker,
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<NyxIdScheduledServiceInvocationCredentialExchangePort>>())
                : new NoopScheduledServiceInvocationCredentialExchangePort());

    public static IServiceCollection AddGAgentServiceProjectionReadModelProviders(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var documentProvider = ProjectionDocumentProviderConfiguration.Resolve(configuration, "GAgentService");
        if (HasAllGAgentServiceProjectionReaders(services, documentProvider.Kind))
            return services;

        services.TryAddSingleton<
            IProjectionDocumentMetadataProvider<WorkflowCatalogCurrentStateDocument>,
            WorkflowCatalogCurrentStateDocumentMetadataProvider>();

        if (documentProvider.ElasticsearchEnabled)
        {
            TryAddElasticsearchDocumentProjectionStore<ServiceCatalogReadModel>(services, configuration, static readModel => readModel.Id);
            TryAddElasticsearchDocumentProjectionStore<ServiceRevisionCatalogReadModel>(services, configuration, static readModel => readModel.Id);
            TryAddElasticsearchDocumentProjectionStore<ServiceDeploymentCatalogReadModel>(services, configuration, static readModel => readModel.Id);
            TryAddElasticsearchDocumentProjectionStore<ServiceServingSetReadModel>(services, configuration, static readModel => readModel.Id);
            TryAddElasticsearchDocumentProjectionStore<ServiceRolloutReadModel>(services, configuration, static readModel => readModel.Id);
            TryAddElasticsearchDocumentProjectionStore<ServiceRolloutCommandObservationReadModel>(services, configuration, static readModel => readModel.Id);
            TryAddElasticsearchDocumentProjectionStore<ServiceTrafficViewReadModel>(services, configuration, static readModel => readModel.Id);
            TryAddElasticsearchDocumentProjectionStore<ServiceInvocationCatalogReadModel>(services, configuration, static readModel => readModel.Id);
            TryAddElasticsearchDocumentProjectionStore<ServiceRunCurrentStateReadModel>(services, configuration, static readModel => readModel.Id);
            TryAddElasticsearchDocumentProjectionStore<GAgentRunTerminalReadModel>(services, configuration, static readModel => readModel.Id);
            TryAddElasticsearchDocumentProjectionStore<LlmSessionCurrentStateReadModel>(services, configuration, static readModel => readModel.Id);
            TryAddElasticsearchDocumentProjectionStore<ResponsesAgentToolStateCurrentStateReadModel>(services, configuration, static readModel => readModel.Id);
            TryAddElasticsearchDocumentProjectionStore<ScheduledDispatchDocument>(services, configuration, static readModel => readModel.ScheduleId);
            TryAddElasticsearchDocumentProjectionStore<UserConfigCurrentStateDocument>(services, configuration, static readModel => readModel.Id);
            TryAddElasticsearchDocumentProjectionStore<WorkflowCatalogCurrentStateDocument>(services, configuration, static readModel => readModel.Id);
        }
        else
        {
            TryAddInMemoryDocumentProjectionStore<ServiceCatalogReadModel>(services, static readModel => readModel.Id);
            TryAddInMemoryDocumentProjectionStore<ServiceRevisionCatalogReadModel>(services, static readModel => readModel.Id);
            TryAddInMemoryDocumentProjectionStore<ServiceDeploymentCatalogReadModel>(services, static readModel => readModel.Id);
            TryAddInMemoryDocumentProjectionStore<ServiceServingSetReadModel>(services, static readModel => readModel.Id);
            TryAddInMemoryDocumentProjectionStore<ServiceRolloutReadModel>(services, static readModel => readModel.Id);
            TryAddInMemoryDocumentProjectionStore<ServiceRolloutCommandObservationReadModel>(services, static readModel => readModel.Id);
            TryAddInMemoryDocumentProjectionStore<ServiceTrafficViewReadModel>(services, static readModel => readModel.Id);
            TryAddInMemoryDocumentProjectionStore<ServiceInvocationCatalogReadModel>(services, static readModel => readModel.Id);
            TryAddInMemoryDocumentProjectionStore<ServiceRunCurrentStateReadModel>(services, static readModel => readModel.Id);
            TryAddInMemoryDocumentProjectionStore<GAgentRunTerminalReadModel>(services, static readModel => readModel.Id);
            TryAddInMemoryDocumentProjectionStore<LlmSessionCurrentStateReadModel>(services, static readModel => readModel.Id);
            TryAddInMemoryDocumentProjectionStore<ResponsesAgentToolStateCurrentStateReadModel>(services, static readModel => readModel.Id);
            TryAddInMemoryDocumentProjectionStore<ScheduledDispatchDocument>(services, static readModel => readModel.ScheduleId);
            TryAddInMemoryDocumentProjectionStore<UserConfigCurrentStateDocument>(services, static readModel => readModel.Id);
            TryAddInMemoryDocumentProjectionStore<WorkflowCatalogCurrentStateDocument>(services, static readModel => readModel.Id);
        }

        return services;
    }

    private static bool HasAllGAgentServiceProjectionReaders(
        IServiceCollection services,
        ProjectionDocumentProviderKind providerKind)
    {
        return HasProjectionDocumentReaderForProvider<ServiceCatalogReadModel>(services, providerKind)
               && HasProjectionDocumentReaderForProvider<ServiceRevisionCatalogReadModel>(services, providerKind)
               && HasProjectionDocumentReaderForProvider<ServiceDeploymentCatalogReadModel>(services, providerKind)
               && HasProjectionDocumentReaderForProvider<ServiceServingSetReadModel>(services, providerKind)
               && HasProjectionDocumentReaderForProvider<ServiceRolloutReadModel>(services, providerKind)
               && HasProjectionDocumentReaderForProvider<ServiceRolloutCommandObservationReadModel>(services, providerKind)
               && HasProjectionDocumentReaderForProvider<ServiceTrafficViewReadModel>(services, providerKind)
               && HasProjectionDocumentReaderForProvider<ServiceInvocationCatalogReadModel>(services, providerKind)
               && HasProjectionDocumentReaderForProvider<ServiceRunCurrentStateReadModel>(services, providerKind)
               && HasProjectionDocumentReaderForProvider<GAgentRunTerminalReadModel>(services, providerKind)
               && HasProjectionDocumentReaderForProvider<LlmSessionCurrentStateReadModel>(services, providerKind)
               && HasProjectionDocumentReaderForProvider<ResponsesAgentToolStateCurrentStateReadModel>(services, providerKind)
               && HasProjectionDocumentReaderForProvider<ScheduledDispatchDocument>(services, providerKind)
               && HasProjectionDocumentReaderForProvider<UserConfigCurrentStateDocument>(services, providerKind)
               && HasProjectionDocumentReaderForProvider<WorkflowCatalogCurrentStateDocument>(services, providerKind);
    }

    private static bool HasAnyProjectionDocumentReader<TReadModel>(IServiceCollection services)
        where TReadModel : class, IProjectionReadModel<TReadModel>, new()
    {
        return services.Any(x => x.ServiceType == typeof(IProjectionDocumentReader<TReadModel, string>));
    }

    private static bool HasProjectionDocumentReaderForProvider<TReadModel>(
        IServiceCollection services,
        ProjectionDocumentProviderKind providerKind)
        where TReadModel : class, IProjectionReadModel<TReadModel>, new()
    {
        return providerKind switch
        {
            ProjectionDocumentProviderKind.Elasticsearch => services.Any(x => x.ServiceType == typeof(ElasticsearchProjectionDocumentStore<TReadModel, string>)),
            ProjectionDocumentProviderKind.InMemory => services.Any(x => x.ServiceType == typeof(InMemoryProjectionDocumentStore<TReadModel, string>)),
            _ => false,
        };
    }

    private static void EnsureCompatibleProjectionDocumentReaderProvider<TReadModel>(
        IServiceCollection services,
        ProjectionDocumentProviderKind providerKind)
        where TReadModel : class, IProjectionReadModel<TReadModel>, new()
    {
        if (!HasAnyProjectionDocumentReader<TReadModel>(services))
            return;
        if (HasProjectionDocumentReaderForProvider<TReadModel>(services, providerKind))
            return;

        throw new InvalidOperationException(
            $"Projection document reader for {typeof(TReadModel).Name} is already registered with a different provider.");
    }

    private static void TryAddElasticsearchDocumentProjectionStore<TReadModel>(
        IServiceCollection services,
        IConfiguration configuration,
        Func<TReadModel, string> keySelector)
        where TReadModel : class, IProjectionReadModel<TReadModel>, new()
    {
        EnsureCompatibleProjectionDocumentReaderProvider<TReadModel>(services, ProjectionDocumentProviderKind.Elasticsearch);
        if (HasProjectionDocumentReaderForProvider<TReadModel>(services, ProjectionDocumentProviderKind.Elasticsearch))
            return;

        services.AddElasticsearchDocumentProjectionStore<TReadModel, string>(
            optionsFactory: _ => ProjectionDocumentProviderConfiguration.BindRequiredElasticsearchOptions(configuration),
            metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataProvider<TReadModel>>().Metadata,
            keySelector: keySelector,
            keyFormatter: static key => key);
    }

    private static void TryAddInMemoryDocumentProjectionStore<TReadModel>(
        IServiceCollection services,
        Func<TReadModel, string> keySelector)
        where TReadModel : class, IProjectionReadModel<TReadModel>, new()
    {
        EnsureCompatibleProjectionDocumentReaderProvider<TReadModel>(services, ProjectionDocumentProviderKind.InMemory);
        if (HasProjectionDocumentReaderForProvider<TReadModel>(services, ProjectionDocumentProviderKind.InMemory))
            return;

        services.AddInMemoryDocumentProjectionStore<TReadModel, string>(
            keySelector: keySelector,
            keyFormatter: static key => key,
            defaultSortSelector: static readModel => readModel.UpdatedAt);
    }

}

internal sealed class ScheduledDispatchInvokeAdmissionAuthorizer : IInvokeAdmissionAuthorizer
{
    public Task AuthorizeAsync(
        string serviceKey,
        string deploymentId,
        PreparedServiceRevisionArtifact artifact,
        ServiceEndpointDescriptor endpoint,
        ServiceInvocationRequest request,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
