using Aevatar.GAgents.WorkOrder;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Studio.Application.Delivery;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Authoring;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Studio.Application.Studio.WorkflowBoards;
using Aevatar.Studio.Application.Studio.WorkflowBoards.Services;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Studio.Application.Studio.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddStudioApplication(this IServiceCollection services)
    {
        services.AddSingleton<WorkflowGraphMapper>();
        services.AddSingleton<TextDiffService>();
        services.AddSingleton<WorkflowEditorService>();
        services.AddSingleton<WorkspaceService>();
        services.AddSingleton<ExecutionService>();
        services.AddSingleton<ConnectorService>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IExternalWorkflowCapabilitySource,
            ConnectorExternalWorkflowCapabilitySource>());
        services.AddSingleton<RoleCatalogService>();
        // Refactor (iter21/cluster-001):
        //   Old pattern: Host registered Ask AI authoring orchestrators and fake actor services.
        //   New principle: Application owns request-scoped authoring preview orchestration behind typed ports.
        services.AddSingleton<WorkflowAuthoringPromptCatalog>();
        services.AddSingleton<ScriptAuthoringPromptCatalog>();
        services.AddSingleton<WorkflowAuthoringPreviewGenerator>();
        // Script authoring preview needs the scripting capability's compiler; hosts composed
        // without scripting get no script generator and reject script previews explicitly.
        if (services.Any(x => x.ServiceType == typeof(Aevatar.Scripting.Core.Compilation.IScriptBehaviorCompiler)))
            services.AddSingleton<ScriptAuthoringPreviewGenerator>();
        services.TryAddSingleton<IStudioAuthoringPreviewApplicationService, StudioAuthoringPreviewApplicationService>();
        services.TryAddSingleton<IStudioMemberService, StudioMemberService>();
        services.TryAddSingleton<IStudioMemberInvocationReadinessQueryPort,
            StudioMemberInvocationReadinessQueryPort>();
        services.TryAddSingleton<IStudioWorkflowProvisioningService, StudioWorkflowProvisioningService>();
        services.TryAddSingleton<IWorkflowDeliveryPackageCatalog, WorkflowDeliveryPackageCatalog>();
        services.TryAddSingleton<IWorkflowDeliveryConfigurationRenderer, WorkflowDeliveryConfigurationRenderer>();
        services.TryAddSingleton<IWorkflowDeliveryService, WorkflowDeliveryService>();
        services.TryAddSingleton<
            IWorkflowInstallationReadinessReconciler,
            WorkflowInstallationReadinessReconciler>();
        services.TryAddSingleton<
            IWorkflowAcceptanceArtifactMaterializer,
            WorkflowAcceptanceArtifactMaterializer>();
        services.TryAddSingleton<
            IWorkflowDeliveryProvisioningExecutor,
            WorkflowDeliveryProvisioningExecutor>();
        services.TryAddSingleton<
            IStudioWorkflowScheduleProvisioningExecutor,
            StudioWorkflowScheduleProvisioningExecutor>();
        // Narrow, tool-facing port (Abstractions) adapting IStudioWorkflowProvisioningService so the
        // aevatar_provision_workflow_schedule agent tool can depend only on Aevatar.Studio.Application.Abstractions.
        services.TryAddSingleton<IWorkflowScheduleProvisioningPort, WorkflowScheduleProvisioningPort>();
        services.TryAddSingleton<IStudioTeamService, StudioTeamService>();
        services.TryAddSingleton<WorkOrderAssignmentValidator>();
        services.TryAddSingleton<IWorkOrderExecutionPort, ValidatedWorkOrderExecutionPort>();
        services.AddOptions<WorkOrderExecutionWorkerOptions>();
        services.TryAddSingleton<IWorkOrderExecutionQueue, WorkOrderExecutionQueue>();
        services.TryAddSingleton<WorkOrderExecutionScheduler>();
        services.TryAddSingleton<IWorkOrderExecutionScheduler>(provider =>
            provider.GetRequiredService<WorkOrderExecutionScheduler>());
        services.TryAddSingleton<WorkOrderExecutionService>();
        services.TryAddSingleton<IContentArtifactService, ContentArtifactService>();
        services.TryAddSingleton<IContentArtifactPinService, ContentArtifactPinService>();
        services.TryAddSingleton<IWorkOrderService, WorkOrderService>();
        services.TryAddSingleton<IStudioTeamProvisioningPort, StudioTeamProvisioningPort>();
        services.TryAddSingleton<IStudioMemberProvisioningPort, StudioMemberProvisioningPort>();
        services.TryAddSingleton<IStudioMemberWorkflowBindingPort, StudioMemberWorkflowBindingPort>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IScopeWorkflowPublishedServiceDescriptorSource,
            StudioMemberScopeWorkflowDescriptorSource>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IScopeWorkflowPublishedServiceDescriptorSource,
            CatalogueScopeWorkflowDescriptorSource>());
        services.TryAddSingleton<IStudioMemberWorkflowDurableAdmissionPort,
            StudioMemberWorkflowDurableAdmissionPort>();
        services.TryAddSingleton(new StudioMemberWorkflowSchedulePolicy());
        services.TryAddSingleton<StudioMemberWorkflowSchedulePort>();
        services.TryAddSingleton<IStudioMemberWorkflowSchedulePort>(provider =>
            provider.GetRequiredService<StudioMemberWorkflowSchedulePort>());
        services.TryAddSingleton<IStudioMemberAutomationQueryPort>(provider =>
            provider.GetRequiredService<IStudioMemberWorkflowSchedulePort>());
        services.TryAddSingleton<IStudioTeamGAgentStreamInvocationService, StudioTeamGAgentStreamInvocationService>();
        services.TryAddSingleton<IWorkflowBoardClock, SystemWorkflowBoardClock>();
        services.TryAddSingleton<IWorkflowBoardRosterQueryPort, StudioWorkflowBoardRosterQueryPort>();
        services.TryAddSingleton<IWorkflowBoardSnapshotQueryPort>(provider =>
            new WorkflowBoardSnapshotQueryService(
                provider.GetRequiredService<IWorkflowBoardRosterQueryPort>(),
                provider.GetService<IWorkflowBoardExecutionQueryPort>(),
                provider.GetRequiredService<IWorkflowBoardClock>()));
        services.AddOptions<UserLlmSettingsOptions>();
        services.Replace(ServiceDescriptor.Singleton<ITeamEntryMemberResolver, StudioTeamEntryMemberResolver>());
        services.TryAddSingleton<UserLlmPreferenceWriter>();
        services.TryAddSingleton<IChannelUserLlmPreferencePort, ChannelUserLlmPreferencePort>();
        services.TryAddSingleton<IUserConfigService, UserConfigService>();
        services.TryAddSingleton<IUserLlmPreferenceService, UserLlmPreferenceService>();
        services.TryAddSingleton<LLMModelSourceResolver>();
        services.TryAddSingleton<
            ILLMModelCatalogPolicyApplicationService,
            LLMModelCatalogPolicyApplicationService>();
        services.TryAddSingleton<
            ILLMModelDiscoveryApplicationService,
            LLMModelDiscoveryApplicationService>();
        services.TryAddSingleton<
            ILLMModelRouteApplicationService,
            LLMModelRouteApplicationService>();

        // Override the platform resolver so existing member-first invoke /
        // runs / binding routes resolve to the same
        // publishedServiceId Studio's bind path persisted on the member
        // authority.
        services.Replace(ServiceDescriptor.Singleton<IMemberPublishedServiceResolver, StudioAwareMemberPublishedServiceResolver>());
        return services;
    }
}
