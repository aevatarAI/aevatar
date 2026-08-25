using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Authentication.ScopeServiceTokens;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgentService.Application.Bindings;
using Aevatar.GAgentService.Application.AgentProfiles;
using Aevatar.GAgentService.Application.Services;
using Aevatar.GAgentService.Application.ScopeGAgents;
using Aevatar.GAgentService.Application.Responses;
using Aevatar.GAgentService.Application.Scripts;
using Aevatar.GAgentService.Application.Schedules;
using Aevatar.GAgentService.Application.Schedules.Authorization;
using Aevatar.GAgentService.Application.Workflows;
using Aevatar.GAgentService.Core.Assemblers;
using Aevatar.GAgentService.Core.Models;
using Aevatar.GAgentService.Core.Schedules;
using Aevatar.GAgentService.Core.Schedules.Authorization;
using Aevatar.GAgentService.Core.Ports;
using Aevatar.GAgentService.Core.Services;
using Aevatar.GAgentService.Infrastructure.Activation;
using Aevatar.GAgentService.Infrastructure.Adapters;
using Aevatar.GAgentService.Infrastructure.Dispatch;
using Aevatar.GAgentService.Infrastructure.DependencyInjection;
using Aevatar.GAgentService.Infrastructure.Orchestration;
using Aevatar.GAgentService.Infrastructure.Schedules;
using Aevatar.GAgentService.Infrastructure.Schedules.Authorization;
using Aevatar.GAgentService.Infrastructure.Credentials;
using Aevatar.Workflow.Abstractions.Credentials;
using Aevatar.GAgentService.Hosting.Backfill;
using Aevatar.GAgentService.Hosting.Demo;
using Aevatar.GAgentService.Hosting.Responses;
using Aevatar.GAgentService.Hosting.Endpoints.Schedules;
using Aevatar.GAgentService.Projection.Contexts;
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
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Domain.Studio.Compatibility;
using Aevatar.Studio.Infrastructure.Serialization;
using Aevatar.Studio.Projection.Metadata;
using Aevatar.Studio.Projection.Projectors;
using Aevatar.Studio.Projection.ReadModels;
using Aevatar.Scripting.Hosting.DependencyInjection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.Abstractions.Runs;
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

        // Scripting is an optional capability: this bundle bridges to it only when the host
        // composed AddScriptCapability beforehand. It must never pull scripting in by itself —
        // hosts that disable scripting (mainnet security lockdown) get no scripting services,
        // endpoints, or hooks from this registration.
        var scriptingCapabilityRegistered = services.Any(x =>
            x.ServiceType == typeof(Aevatar.Scripting.Hosting.DependencyInjection.ServiceCollectionExtensions.ScriptCapabilityRegistrationsMarker));

        if (!services.Any(x => x.ServiceType == typeof(WorkflowCapabilityServiceCollectionExtensions.WorkflowCapabilityRegistrationsMarker)))
            services.AddWorkflowCapability(configuration);

        services.AddAevatarAgentKindRegistry(builder => builder.ScanAssemblies(typeof(Aevatar.GAgentService.Core.GAgents.ServiceDefinitionGAgent).Assembly));
        services.AddOptions<GAgentServiceDemoOptions>()
            .Bind(configuration.GetSection("GAgentService:Demo"));
        services.AddOptions<ServiceExternalExposureOptions>()
            .Bind(configuration.GetSection(ServiceExternalExposureOptions.SectionName));
        services.TryAddSingleton(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ServiceExternalExposureOptions>>().Value;
            return ServiceExternalExposureRetrySettings.Create(
                options.RetryMaxAttempts,
                TimeSpan.FromSeconds(options.RetryBaseDelaySeconds),
                TimeSpan.FromSeconds(options.RetryMaxDelaySeconds));
        });
        services.AddOptions<NyxIdRegistrationTokenOptions>()
            .Bind(configuration.GetSection(NyxIdRegistrationTokenOptions.SectionName));
        if (configuration.GetSection(ScopeServiceTokenOptions.SectionName).Get<ScopeServiceTokenOptions>()?.Enabled == true)
            services.AddScopeServiceTokens(configuration);
        services.AddNyxIdAuthorizationCatalogHosting(configuration);
        services.AddGAgentServiceGovernanceCapability(configuration);
        services.TryAddSingleton<PreparedServiceRevisionArtifactAssembler>();
        services.TryAddSingleton<ServiceInvokeReadinessEvaluator>();
        services.TryAddSingleton<IServiceServingTargetResolver, DefaultServiceServingTargetResolver>();
        services.TryAddSingleton<IServiceCommandTargetProvisioner, DefaultServiceCommandTargetProvisioner>();
        services.TryAddSingleton<IServiceRuntimeActivator>(sp => new DefaultServiceRuntimeActivator(
            sp.GetRequiredService<Aevatar.Foundation.Abstractions.IActorRuntime>(),
            sp.GetService<IScriptDefinitionSnapshotPort>(),
            sp.GetService<IScriptRuntimeProvisioningPort>(),
            sp.GetRequiredService<IWorkflowDefinitionProvisioningPort>()));
        if (scriptingCapabilityRegistered)
            services.TryAddEnumerable(ServiceDescriptor.Singleton<ICommittedStatePublicationHook, ScriptingServiceRevisionRepublishHook>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICommittedStatePublicationHook, ServiceExposureReconcileHook>());
        services.TryAddSingleton<ServiceExternalExposureIntentService>();
        services.TryAddSingleton<IServiceExternalExposureIntentPort>(sp => sp.GetRequiredService<ServiceExternalExposureIntentService>());
        services.TryAddSingleton<NyxIdToolOptions>();
        services.TryAddSingleton<INyxIdServiceRegistrationPort, NyxIdServiceRegistrationAdapter>();
        services.TryAddSingleton<INyxIdRegistrationTokenAccessor, ConfiguredNyxIdRegistrationTokenAccessor>();
        AddServiceRunWritePorts(services);
        services.TryAddSingleton<ILlmSessionRegistrationPort, LlmSessionRegistrationAdapter>();
        services.TryAddSingleton<IResponsesAgentToolStateCommandPort, ResponsesAgentToolStateCommandAdapter>();
        services.TryAddSingleton<ILlmSessionRunObservationService, LlmSessionRunObservationService>();
        services.TryAddSingleton<ILlmRunCore>(sp =>
        {
            var providerFactory = sp.GetService<Aevatar.AI.Abstractions.LLMProviders.ILLMProviderFactory>();
            return providerFactory == null
                ? MissingLlmProviderRunCore.Instance
                : new LlmRunCore(
                    providerFactory,
                    sp.GetServices<IResponsesToolProvider>(),
                    sp.GetRequiredService<IToolSetRegistry>(),
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<LlmRunCore>>(),
                    sp.GetRequiredService<Aevatar.AI.Abstractions.ToolProviders.IAgentToolExecutionPort>());
        });
        services.TryAddSingleton<LlmRunExecutor>();
        services.TryAddSingleton<ILlmRunExecutor>(sp => sp.GetRequiredService<LlmRunExecutor>());
        services.TryAddSingleton<ILlmRunExecutionService>(sp => sp.GetRequiredService<LlmRunExecutor>());
        services.TryAddSingleton(WorkflowCompatibilityProfile.AevatarV1);
        services.TryAddSingleton<IWorkflowYamlDocumentService, YamlWorkflowDocumentService>();
        services.TryAddSingleton<IScopeWorkflowCatalogueRowCommandPort, ActorDispatchScopeWorkflowCatalogueRowCommandPort>();
        services.TryAddSingleton<ScopeWorkflowCatalogueRowMaterializer>();
        // Off-grain run execution (epic #2271 root fix): the scheduler enqueues to an
        // in-process bounded queue that a hosted background worker drains off any Orleans
        // grain turn, instead of provisioning a per-run execution grain that blocked its
        // own event-handler turn for the whole run.
        services.AddOptions<LlmRunExecutionWorkerOptions>()
            .Bind(configuration.GetSection(LlmRunExecutionWorkerOptions.SectionName));
        services.TryAddSingleton<ILlmRunExecutionQueue, LlmRunExecutionQueue>();
        services.AddProjectionArtifactMaterializer<
            ServiceDeploymentCatalogProjectionContext,
            ScopeWorkflowCatalogueServiceSourceProjector>();
        services.AddProjectionArtifactMaterializer<
            ServiceRevisionCatalogProjectionContext,
            ScopeWorkflowCatalogueRevisionSourceProjector>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ScopeWorkflowCatalogueBackfillHostedService>());
        services.TryAddSingleton<LlmRunExecutionScheduler>();
        services.TryAddSingleton<ILlmRunExecutionScheduler>(sp => sp.GetRequiredService<LlmRunExecutionScheduler>());
        services.AddHostedService<LlmRunExecutionWorker>();
        services.TryAddSingleton<IResponsesToolClassificationService, ResponsesToolClassificationService>();
        services.AddToolSetRegistry();
        services.TryAddSingleton<IResponsesDirectToolPlanService, ResponsesDirectToolPlanService>();
        services.TryAddSingleton<IAgentProfileTurnSnapshotResolver, AgentProfileTurnSnapshotResolver>();
        services.TryAddSingleton<IResponsesOwnedToolCatalogPlanner>(sp =>
            new ResponsesOwnedToolCatalogPlanner(
                sp.GetService<IAgentProfileTurnSnapshotResolver>(),
                sp.GetService<IAgentProfileTurnToolCatalogPlanner>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ResponsesOwnedToolCatalogPlanner>>()));
        services.TryAddSingleton<IServiceInvocationDispatcher>(sp => new DefaultServiceInvocationDispatcher(
            sp.GetRequiredService<IActorDispatchPort>(),
            sp.GetService<IScriptRuntimeCommandPort>(),
            sp.GetRequiredService<IWorkflowRunProvisioningPort>(),
            sp.GetRequiredService<IServiceRunRegistrationPort>(),
            sp.GetRequiredService<IWorkflowArtifactCompatibilityPreflight>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<DefaultServiceInvocationDispatcher>>()));
        services.TryAddSingleton<IExecutionActivityScopeResolver, ExecutionActivityScopeResolver>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Aevatar.Foundation.Abstractions.Hooks.IGAgentExecutionHook, ExecutionActivityPublisherHook>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IServiceImplementationAdapter, StaticServiceImplementationAdapter>());
        if (scriptingCapabilityRegistered)
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IServiceImplementationAdapter, ScriptingServiceImplementationAdapter>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IServiceImplementationAdapter, WorkflowServiceImplementationAdapter>());
        services.TryAddSingleton<ServiceInvocationResolutionService>();
        services.TryAddSingleton<IServiceInvocationResolutionPort>(sp =>
            sp.GetRequiredService<ServiceInvocationResolutionService>());
        services.TryAddSingleton<ServiceInvokeReadinessErrorMapper>();
        services.TryAddSingleton<IServiceCommandPort, ServiceCommandApplicationService>();
        services.TryAddSingleton<IServiceLifecycleQueryPort, ServiceLifecycleQueryApplicationService>();
        services.TryAddSingleton<IServiceServingQueryPort, ServiceServingQueryApplicationService>();
        services.TryAddSingleton<IServiceInvocationPort, ServiceInvocationApplicationService>();
        services.TryAddSingleton<IScheduledServiceInvocationDispatchPort, ScheduledServiceInvocationDispatchPort>();
        services.TryAddSingleton<IWorkflowCallerAccessTokenProvider, NyxIdWorkflowCallerAccessTokenProvider>();
        services.AddScheduledCredentialExchangePort();
        services.TryAddSingleton<IScheduledDispatchCredentialRequirementPolicy, DefaultScheduledDispatchCredentialRequirementPolicy>();
        services.AddScheduledCredentialAdmissionPort();
        services.TryAddSingleton<IScheduledDispatchTargetPreparationService, ScheduledDispatchTargetPreparationService>();
        services.TryAddSingleton<IScheduledDispatchApplicationService, ScheduledDispatchApplicationService>();
        services.TryAddSingleton<IScheduledDispatchActorPort, ScheduledDispatchActorPort>();
        services.TryAddTransient<ScheduledDispatchGAgent>();
        services.TryAddSingleton<IStaticGAgentStreamInvocationPort<AGUIEvent>, StaticGAgentStreamInvocationApplicationService>();
        services.AddScopeGAgentDraftRunInteraction();
        if (scriptingCapabilityRegistered)
            services.AddScriptServiceRunInteraction();
        services.AddOptions<ScopeWorkflowCapabilityOptions>()
            .Bind(configuration.GetSection(ScopeWorkflowCapabilityOptions.SectionName));
        services.TryAddSingleton<ScopeWorkflowQueryApplicationService>();
        services.TryAddSingleton<IScopeWorkflowQueryPort>(sp => sp.GetRequiredService<ScopeWorkflowQueryApplicationService>());
        services.TryAddSingleton<IScopeWorkflowCatalogueCommittedSourcePort>(sp => sp.GetRequiredService<ScopeWorkflowQueryApplicationService>());
        services.TryAddSingleton<IScopeWorkflowCommandPort, ScopeWorkflowCommandApplicationService>();
        services.TryAddSingleton<IScopeWorkflowArchiveCommandPort, ScopeWorkflowArchiveApplicationService>();
        services.TryAddSingleton<IScopeWorkflowSaveAndBindPort, ScopeWorkflowSaveAndBindApplicationService>();
        services.Replace(ServiceDescriptor.Singleton(
            typeof(SkillWorkflowMountAdapter),
            typeof(SkillWorkflowMountAdapter)));
        services.Replace(ServiceDescriptor.Singleton<ISkillWorkflowMountPort>(sp =>
            sp.GetRequiredService<SkillWorkflowMountAdapter>()));
        services.Replace(ServiceDescriptor.Singleton<ISkillWorkflowConfirmationPort>(sp =>
            sp.GetRequiredService<SkillWorkflowMountAdapter>()));
        services.TryAddSingleton<IScopeBindingCommandPort>(sp => new ScopeBindingCommandApplicationService(
            sp.GetRequiredService<IServiceCommandPort>(),
            sp.GetRequiredService<IServiceLifecycleQueryPort>(),
            sp.GetRequiredService<IServiceGovernanceCommandPort>(),
            sp.GetRequiredService<IServiceGovernanceQueryPort>(),
            sp.GetService<IScopeScriptQueryPort>(),
            sp.GetService<IScriptDefinitionSnapshotPort>(),
            sp.GetRequiredService<IWorkflowDefinitionParser>(),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ScopeWorkflowCapabilityOptions>>(),
            sp.GetRequiredService<IWorkflowExternalCapabilityAdmissionService>(),
            sp.GetService<IAgentKindRegistry>(),
            sp.GetService<IServiceExternalExposureIntentPort>()));
        services.TryAddSingleton<IScopeBindingReadinessQueryPort, ScopeBindingReadinessQueryService>();
        services.TryAddSingleton<IMemberPublishedServiceResolver, DefaultMemberPublishedServiceResolver>();
        if (scriptingCapabilityRegistered)
        {
            services.AddOptions<ScopeScriptCapabilityOptions>()
                .Bind(configuration.GetSection(ScopeScriptCapabilityOptions.SectionName));
            services.TryAddSingleton<ScopeScriptQueryApplicationService>();
            services.TryAddSingleton<IScopeScriptQueryPort>(sp => sp.GetRequiredService<ScopeScriptQueryApplicationService>());
            services.TryAddSingleton<IScopeScriptCommandPort, ScopeScriptCommandApplicationService>();
            services.TryAddSingleton<IScopeScriptSaveObservationPort, ScopeScriptSaveObservationService>();
        }
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, GAgentServiceDemoBootstrapHostedService>());
        return services;
    }

    public static IServiceCollection AddScheduledDispatchCapability(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddAevatarAgentKindRegistry(builder =>
        {
            builder.Register<ScheduledDispatchGAgent>();
        });
        services.AddNyxIdAuthorizationCatalogHosting(configuration);
        services.TryAddSingleton<PreparedServiceRevisionArtifactAssembler>();
        services.TryAddSingleton<IServiceServingTargetResolver, DefaultServiceServingTargetResolver>();
        AddServiceRunWritePorts(services);
        services.TryAddSingleton<ServiceInvocationResolutionService>();
        services.TryAddSingleton<IServiceInvocationResolutionPort>(sp =>
            sp.GetRequiredService<ServiceInvocationResolutionService>());
        services.TryAddSingleton<ServiceInvokeReadinessErrorMapper>();
        services.TryAddSingleton<IInvokeAdmissionAuthorizer, ScheduledDispatchInvokeAdmissionAuthorizer>();
        services.TryAddSingleton<IServiceInvocationDispatcher>(sp => new DefaultServiceInvocationDispatcher(
            sp.GetRequiredService<IActorDispatchPort>(),
            sp.GetService<IScriptRuntimeCommandPort>(),
            sp.GetRequiredService<IWorkflowRunProvisioningPort>(),
            sp.GetRequiredService<IServiceRunRegistrationPort>(),
            sp.GetRequiredService<IWorkflowArtifactCompatibilityPreflight>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<DefaultServiceInvocationDispatcher>>()));
        services.TryAddSingleton<IServiceInvocationPort, ServiceInvocationApplicationService>();
        services.TryAddSingleton<IScheduledServiceInvocationDispatchPort, ScheduledServiceInvocationDispatchPort>();
        services.AddScheduledCredentialExchangePort();
        services.TryAddSingleton<IScheduledDispatchCredentialRequirementPolicy, DefaultScheduledDispatchCredentialRequirementPolicy>();
        services.AddScheduledCredentialAdmissionPort();
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

    private static void AddScheduledCredentialAdmissionPort(this IServiceCollection services) =>
        services.TryAddSingleton<IScheduledDispatchCredentialAdmissionPort>(sp =>
            sp.GetService<IExternalIdentityBindingQueryPort>() is { } bindingQueryPort
                ? new NyxIdScheduledDispatchCredentialAdmissionPort(bindingQueryPort)
                : new NoopScheduledDispatchCredentialAdmissionPort());

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
        services.TryAddSingleton<
            IProjectionDocumentMetadataProvider<WorkflowActorBindingDocument>,
            WorkflowActorBindingDocumentMetadataProvider>();
        services.TryAddSingleton<
            IProjectionDocumentMetadataProvider<ScopeWorkflowCatalogueSourceDocument>,
            ScopeWorkflowCatalogueSourceDocumentMetadataProvider>();
        services.TryAddSingleton<
            IProjectionDocumentMetadataProvider<ScopeWorkflowCatalogueRowDocument>,
            ScopeWorkflowCatalogueRowDocumentMetadataProvider>();
        services.TryAddSingleton<
            IProjectionDocumentMetadataProvider<StudioWorkspaceCurrentStateDocument>,
            StudioWorkspaceCurrentStateDocumentMetadataProvider>();

        if (documentProvider.ElasticsearchEnabled)
        {
            TryAddElasticsearchDocumentProjectionStore<AgentProfileCatalogReadModel>(services, configuration, static readModel => readModel.Id);
            TryAddElasticsearchDocumentProjectionStore<AgentProfileManagementReadModel>(services, configuration, static readModel => readModel.Id);
            TryAddElasticsearchDocumentProjectionStore<AgentProfileExecutionReadModel>(services, configuration, static readModel => readModel.Id);
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
            TryAddElasticsearchDocumentProjectionStore<NyxIdAuthorizationCatalogDocument>(services, configuration, static readModel => readModel.Id);
            services.AddElasticsearchDocumentProjectionRepairStore<
                NyxIdAuthorizationCatalogDocument,
                string>();
            services.AddNyxIdAuthorizationCatalogVersionRegressionRepairPorts();
            services.TryAddSingleton<
                INyxIdAuthorizationCatalogVersionRegressionStorePort,
                ElasticsearchNyxIdAuthorizationCatalogVersionRegressionStorePort>();
            services.TryAddSingleton<
                INyxIdAuthorizationCatalogVersionRegressionRepairService,
                NyxIdAuthorizationCatalogVersionRegressionRepairService>();
            TryAddElasticsearchDocumentProjectionStore<UserConfigCurrentStateDocument>(services, configuration, static readModel => readModel.Id);
            TryAddElasticsearchDocumentProjectionStore<WorkflowCatalogCurrentStateDocument>(services, configuration, static readModel => readModel.Id);
            TryAddElasticsearchDocumentProjectionStore<WorkflowActorBindingDocument>(services, configuration, static readModel => readModel.Id);
            TryAddElasticsearchDocumentProjectionStore<ScopeWorkflowCatalogueSourceDocument>(services, configuration, static readModel => readModel.Id);
            TryAddElasticsearchDocumentProjectionStore<ScopeWorkflowCatalogueRowDocument>(services, configuration, static readModel => readModel.Id);
            TryAddElasticsearchDocumentProjectionStore<StudioWorkspaceCurrentStateDocument>(services, configuration, static readModel => readModel.Id);
        }
        else
        {
            TryAddInMemoryDocumentProjectionStore<AgentProfileCatalogReadModel>(services, static readModel => readModel.Id);
            TryAddInMemoryDocumentProjectionStore<AgentProfileManagementReadModel>(services, static readModel => readModel.Id);
            TryAddInMemoryDocumentProjectionStore<AgentProfileExecutionReadModel>(services, static readModel => readModel.Id);
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
            TryAddInMemoryDocumentProjectionStore<NyxIdAuthorizationCatalogDocument>(services, static readModel => readModel.Id);
            TryAddInMemoryDocumentProjectionStore<UserConfigCurrentStateDocument>(services, static readModel => readModel.Id);
            TryAddInMemoryDocumentProjectionStore<WorkflowCatalogCurrentStateDocument>(services, static readModel => readModel.Id);
            TryAddInMemoryDocumentProjectionStore<WorkflowActorBindingDocument>(services, static readModel => readModel.Id);
            TryAddInMemoryDocumentProjectionStore<ScopeWorkflowCatalogueSourceDocument>(services, static readModel => readModel.Id);
            TryAddInMemoryDocumentProjectionStore<ScopeWorkflowCatalogueRowDocument>(services, static readModel => readModel.Id);
            TryAddInMemoryDocumentProjectionStore<StudioWorkspaceCurrentStateDocument>(services, static readModel => readModel.Id);
        }

        return services;
    }

    private static bool HasAllGAgentServiceProjectionReaders(
        IServiceCollection services,
        ProjectionDocumentProviderKind providerKind)
    {
        return HasProjectionDocumentReaderForProvider<ServiceCatalogReadModel>(services, providerKind)
               && HasProjectionDocumentReaderForProvider<AgentProfileCatalogReadModel>(services, providerKind)
               && HasProjectionDocumentReaderForProvider<AgentProfileManagementReadModel>(services, providerKind)
               && HasProjectionDocumentReaderForProvider<AgentProfileExecutionReadModel>(services, providerKind)
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
               && HasProjectionDocumentReaderForProvider<NyxIdAuthorizationCatalogDocument>(services, providerKind)
               && HasProjectionDocumentReaderForProvider<UserConfigCurrentStateDocument>(services, providerKind)
               && HasProjectionDocumentReaderForProvider<WorkflowCatalogCurrentStateDocument>(services, providerKind)
               && HasProjectionDocumentReaderForProvider<WorkflowActorBindingDocument>(services, providerKind)
               && HasProjectionDocumentReaderForProvider<ScopeWorkflowCatalogueSourceDocument>(services, providerKind)
               && HasProjectionDocumentReaderForProvider<ScopeWorkflowCatalogueRowDocument>(services, providerKind)
               && HasProjectionDocumentReaderForProvider<StudioWorkspaceCurrentStateDocument>(services, providerKind);
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

    private static void AddServiceRunWritePorts(IServiceCollection services)
    {
        services.TryAddSingleton<ServiceRunRegistrationAdapter>();
        services.TryAddSingleton<IServiceRunRegistrationPort>(sp =>
            sp.GetRequiredService<ServiceRunRegistrationAdapter>());
        services.TryAddSingleton<IServiceRunResultArtifactAttachmentPort>(sp =>
            sp.GetRequiredService<ServiceRunRegistrationAdapter>());
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
