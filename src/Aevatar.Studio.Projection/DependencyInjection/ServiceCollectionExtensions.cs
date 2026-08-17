using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.CQRS.Core.DependencyInjection;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Projection.CommandServices;
using Aevatar.Studio.Projection.Audit;
using Aevatar.Studio.Projection.Metadata;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Projection.Projectors;
using Aevatar.Studio.Projection.QueryPorts;
using Aevatar.Studio.Projection.ReadModels;
using Aevatar.GAgents.StudioMember;
using Aevatar.GAgents.ContentArtifacts;
using Aevatar.GAgents.WorkOrder;
using Aevatar.GAgents.WorkflowDelivery;
using Aevatar.Studio.Workspace;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Studio.Projection.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Studio projection components: materialization runtime,
    /// projectors, query ports, command services, and document metadata providers.
    /// </summary>
    public static IServiceCollection AddStudioProjectionComponents(this IServiceCollection services) =>
        services.AddStudioProjectionComponents(configuration: null);

    public static IServiceCollection AddStudioProjectionComponents(
        this IServiceCollection services,
        IConfiguration? configuration)
    {
        ArgumentNullException.ThrowIfNull(services);

        var optionsBuilder = services.AddOptions<StudioMemberPlatformBindingOptions>();
        if (configuration != null)
            optionsBuilder.Bind(configuration.GetSection(StudioMemberPlatformBindingOptions.SectionName));

        services.AddCqrsCore();
        services.AddAevatarAgentKindRegistry(builder => builder.ScanAssemblies(
            typeof(Aevatar.GAgents.UserConfig.UserConfigGAgent).Assembly,
            typeof(Aevatar.GAgents.StudioMember.StudioMemberGAgent).Assembly,
            typeof(Aevatar.GAgents.StudioTeam.StudioTeamGAgent).Assembly,
            typeof(ContentArtifactGAgent).Assembly,
            typeof(Aevatar.GAgents.WorkOrder.WorkOrderGAgent).Assembly,
            typeof(WorkflowDeliveryGAgent).Assembly,
            typeof(Aevatar.Studio.Workspace.StudioWorkspaceGAgent).Assembly,
            typeof(ScopeWorkflowCatalogueRowGAgent).Assembly));
        services.AddStudioProjectionActorCommandDispatch();

        // Projection read-model runtime (write dispatcher + sink bindings)
        services.AddProjectionReadModelRuntime();

        // Projection clock (shared, idempotent registration)
        services.TryAddSingleton<IProjectionClock, SystemProjectionClock>();

        // Materialization runtime for Studio current-state projections
        services.AddProjectionMaterializationRuntimeCore<
            StudioMaterializationContext,
            StudioMaterializationRuntimeLease,
            ProjectionMaterializationScopeGAgent<StudioMaterializationContext>>(
            scopeKey => new StudioMaterializationContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            context => new StudioMaterializationRuntimeLease(context));
        services.AddProjectionMaterializationRuntimeCore<
            StudioWorkflowBoardMaterializationContext,
            StudioWorkflowBoardMaterializationRuntimeLease,
            ProjectionMaterializationScopeGAgent<StudioWorkflowBoardMaterializationContext>>(
            scopeKey => new StudioWorkflowBoardMaterializationContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            context => new StudioWorkflowBoardMaterializationRuntimeLease(context));

        // ── Projectors ──

        services.AddCurrentStateProjectionMaterializer<
            StudioMaterializationContext,
            UserConfigCurrentStateProjector>();

        services.AddCurrentStateProjectionMaterializer<
            StudioMaterializationContext,
            LLMModelCatalogPolicyCurrentStateProjector>();

        services.AddCurrentStateProjectionMaterializer<
            StudioMaterializationContext,
            GAgentRegistryCurrentStateProjector>();

        services.AddCurrentStateProjectionMaterializer<
            StudioMaterializationContext,
            ConnectorCatalogCurrentStateProjector>();

        services.AddCurrentStateProjectionMaterializer<
            StudioMaterializationContext,
            RoleCatalogCurrentStateProjector>();

        services.AddCurrentStateProjectionMaterializer<
            StudioMaterializationContext,
            UserMemoryCurrentStateProjector>();

        services.AddCurrentStateProjectionMaterializer<
            StudioMaterializationContext,
            ChatConversationCurrentStateProjector>();

        services.AddCurrentStateProjectionMaterializer<
            StudioMaterializationContext,
            NyxIdChatConversationCurrentStateProjector>();

        services.AddCurrentStateProjectionMaterializer<
            StudioMaterializationContext,
            ChatHistoryCreateRecoveryCurrentStateProjector>();

        services.AddCurrentStateProjectionMaterializer<
            StudioMaterializationContext,
            StudioMemberCurrentStateProjector>();

        services.AddCurrentStateProjectionMaterializer<
            StudioMaterializationContext,
            ContentArtifactCurrentStateProjector>();

        services.AddCurrentStateProjectionMaterializer<
            StudioMaterializationContext,
            WorkOrderCurrentStateProjector>();

        services.AddCurrentStateProjectionMaterializer<
            StudioMaterializationContext,
            WorkflowDeliveryCurrentStateProjector>();

        services.AddCurrentStateProjectionMaterializer<
            StudioMaterializationContext,
            StudioMemberBindingRunCurrentStateProjector>();

        services.AddCurrentStateProjectionMaterializer<
            StudioMaterializationContext,
            StudioTeamRosterFanoutMaterializer>();

        services.AddCurrentStateProjectionMaterializer<
            StudioMaterializationContext,
            StudioTeamCurrentStateProjector>();

        services.AddCurrentStateProjectionMaterializer<
            StudioMaterializationContext,
            StudioWorkspaceCurrentStateProjector>();

        services.AddCurrentStateProjectionMaterializer<
            StudioMaterializationContext,
            ScopeWorkflowCatalogueRowCurrentStateProjector>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, StudioMemberCreatedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, StudioMemberImplementationUpdatedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, StudioMemberReassignedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, StudioMemberDeletedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, StudioTeamCreatedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, StudioTeamUpdatedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, StudioTeamArchivedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, StudioMemberRenamedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, StudioMemberBindingCompletedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, StudioMemberBindingFailedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, StudioMemberBindingRejectedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, StudioTeamEntryMemberChangedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, ActorRegisteredAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, ActorUnregisteredAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, ConnectorCatalogSavedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, ConnectorDraftSavedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, ConnectorDraftDeletedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, RoleCatalogSavedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, RoleDraftSavedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, RoleDraftDeletedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, UserConfigUpdatedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, UserConfigGithubUsernameUpdatedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, MemoryEntriesClearedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, ConversationDeletedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, NyxIdChatActionRequestedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, NyxIdChatActionContinuationResolvedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, NyxIdChatActionPostconditionResolvedAuditTranslator>());
        services.AddAuditCommittedFactMaterializer<StudioMaterializationContext>();

        services.AddProjectionArtifactMaterializer<
            StudioWorkflowBoardMaterializationContext,
            WorkflowExecutionBoardMaterializer>();

        // ── Document metadata providers (for index creation in Elasticsearch) ──

        services.TryAddSingleton<
            IProjectionDocumentMetadataProvider<WorkflowExecutionBoardDocument>,
            WorkflowExecutionBoardDocumentMetadataProvider>();

        services.TryAddSingleton<
            IProjectionDocumentMetadataProvider<ScopeWorkflowCatalogueSourceDocument>,
            ScopeWorkflowCatalogueSourceDocumentMetadataProvider>();

        services.TryAddSingleton<
            IProjectionDocumentMetadataProvider<UserConfigCurrentStateDocument>,
            UserConfigCurrentStateDocumentMetadataProvider>();

        services.TryAddSingleton<
            IProjectionDocumentMetadataProvider<LLMModelCatalogPolicyCurrentStateDocument>,
            LLMModelCatalogPolicyCurrentStateDocumentMetadataProvider>();

        services.TryAddSingleton<
            IProjectionDocumentMetadataProvider<GAgentRegistryCurrentStateDocument>,
            GAgentRegistryCurrentStateDocumentMetadataProvider>();

        services.TryAddSingleton<
            IProjectionDocumentMetadataProvider<ConnectorCatalogCurrentStateDocument>,
            ConnectorCatalogCurrentStateDocumentMetadataProvider>();

        services.TryAddSingleton<
            IProjectionDocumentMetadataProvider<RoleCatalogCurrentStateDocument>,
            RoleCatalogCurrentStateDocumentMetadataProvider>();

        services.TryAddSingleton<
            IProjectionDocumentMetadataProvider<UserMemoryCurrentStateDocument>,
            UserMemoryCurrentStateDocumentMetadataProvider>();

        services.TryAddSingleton<
            IProjectionDocumentMetadataProvider<ChatConversationCurrentStateDocument>,
            ChatConversationCurrentStateDocumentMetadataProvider>();

        services.TryAddSingleton<
            IProjectionDocumentMetadataProvider<NyxIdChatConversationCurrentStateDocument>,
            NyxIdChatConversationCurrentStateDocumentMetadataProvider>();

        services.TryAddSingleton<
            IProjectionDocumentMetadataProvider<ChatHistoryCreateRecoveryCurrentStateDocument>,
            ChatHistoryCreateRecoveryCurrentStateDocumentMetadataProvider>();

        services.TryAddSingleton<
            IProjectionDocumentMetadataProvider<StudioMemberCurrentStateDocument>,
            StudioMemberCurrentStateDocumentMetadataProvider>();

        services.TryAddSingleton<
            IProjectionDocumentMetadataProvider<ContentArtifactCurrentStateDocument>,
            ContentArtifactCurrentStateDocumentMetadataProvider>();

        services.TryAddSingleton<
            IProjectionDocumentMetadataProvider<WorkOrderCurrentStateDocument>,
            WorkOrderCurrentStateDocumentMetadataProvider>();

        services.TryAddSingleton<
            IProjectionDocumentMetadataProvider<WorkflowDeliveryCurrentStateDocument>,
            WorkflowDeliveryCurrentStateDocumentMetadataProvider>();

        services.TryAddSingleton<
            IProjectionDocumentMetadataProvider<StudioMemberBindingRunCurrentStateDocument>,
            StudioMemberBindingRunCurrentStateDocumentMetadataProvider>();

        services.TryAddSingleton<
            IProjectionDocumentMetadataProvider<StudioTeamCurrentStateDocument>,
            StudioTeamCurrentStateDocumentMetadataProvider>();

        services.TryAddSingleton<
            IProjectionDocumentMetadataProvider<StudioWorkspaceCurrentStateDocument>,
            StudioWorkspaceCurrentStateDocumentMetadataProvider>();
        services.TryAddSingleton<
            IProjectionDocumentMetadataProvider<ScopeWorkflowCatalogueRowDocument>,
            ScopeWorkflowCatalogueRowDocumentMetadataProvider>();

        services.TryAddSingleton<ScopeWorkflowCatalogueRowMaterializer>();
        services.TryAddSingleton<ProjectionActivationPlanDispatcher>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            ICommittedStatePublicationHook,
            CommittedStateProjectionActivationHook>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionActivationPlanProvider,
            StudioCommittedStateProjectionActivationPlanProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionActivationPlanProvider,
            StudioWorkflowBoardProjectionActivationPlanProvider>());

        // Compile-time-safe actor provisioning used by every Studio actor-backed
        // store. Projection activation is driven by committed-state plans.
        services.TryAddSingleton<IStudioActorBootstrap, StudioActorBootstrap>();

        // Query ports (read side)
        services.TryAddSingleton<IUserConfigQueryPort, ProjectionUserConfigQueryPort>();
        services.TryAddSingleton<ILLMModelCatalogPolicyQueryPort, ProjectionLLMModelCatalogPolicyQueryPort>();
        services.TryAddSingleton<IUserMemoryQueryPort, ProjectionUserMemoryQueryPort>();
        services.TryAddSingleton<IStudioMemberQueryPort, ProjectionStudioMemberQueryPort>();
        services.TryAddSingleton<IContentArtifactQueryPort, ProjectionContentArtifactQueryPort>();
        services.TryAddSingleton<IWorkOrderQueryPort, ProjectionWorkOrderQueryPort>();
        services.TryAddSingleton<IWorkflowDeliveryQueryPort, ProjectionWorkflowDeliveryQueryPort>();
        services.TryAddSingleton<IStudioMemberBindingRunQueryPort, ProjectionStudioMemberBindingRunQueryPort>();
        services.TryAddSingleton<IStudioTeamQueryPort, ProjectionStudioTeamQueryPort>();
        services.TryAddSingleton<IStudioWorkspaceQueryPort, ProjectionStudioWorkspaceQueryPort>();
        services.TryAddSingleton<IWorkflowCatalogueQueryPort, ProjectionWorkflowCatalogueQueryPort>();
        services.TryAddSingleton<IScheduledInvocationMemberEvidenceQueryPort, ProjectionScheduledInvocationMemberQueryPort>();
        services.TryAddSingleton<IScheduledInvocationWorkflowEvidenceQueryPort, ProjectionScheduledInvocationWorkflowQueryPort>();
        services.TryAddSingleton<IScheduledInvocationConnectorEvidenceQueryPort, ProjectionScheduledInvocationConnectorQueryPort>();
        services.TryAddSingleton<IScheduledInvocationOwnerLLMEvidenceQueryPort, ProjectionScheduledInvocationOwnerLLMQueryPort>();

        // Command services (write side)
        services.TryAddSingleton<IUserConfigCommandService, ActorDispatchUserConfigCommandService>();
        services.TryAddSingleton<
            ILLMModelCatalogPolicyCommandPort,
            ActorDispatchLLMModelCatalogPolicyCommandService>();
        services.TryAddSingleton<IStudioMemberCommandPort, ActorDispatchStudioMemberCommandService>();
        services.TryAddSingleton<
            IStudioWorkflowScheduleProvisioningCommandPort,
            ActorDispatchStudioWorkflowScheduleProvisioningCommandService>();
        services.TryAddSingleton<
            IStudioMemberWorkflowScheduleProvisioningPort,
            StudioMemberWorkflowScheduleProvisioningExecutionPort>();
        services.TryAddSingleton<IContentArtifactCommandPort, ActorDispatchContentArtifactCommandService>();
        services.TryAddSingleton<IWorkOrderCommandPort, ActorDispatchWorkOrderCommandService>();
        services.TryAddSingleton<
            IWorkflowDeliveryCommandPort,
            ActorDispatchWorkflowDeliveryCommandService>();
        services.TryAddSingleton<IStudioMemberPlatformBindingCommandPort, ScopeBindingStudioMemberPlatformBindingCommandService>();
        services.TryAddSingleton<IStudioTeamCommandPort, ActorDispatchStudioTeamCommandService>();
        return services;
    }
}
