using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Core.Streaming;
using Aevatar.CQRS.Projection.Runtime.DependencyInjection;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Projection.Configuration;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.Audit;
using Aevatar.GAgentService.Projection.Metadata;
using Aevatar.GAgentService.Projection.Orchestration;
using Aevatar.GAgentService.Projection.Projectors;
using Aevatar.GAgentService.Projection.Queries;
using Aevatar.GAgentService.Projection.ReadModels;
using Aevatar.AGUI.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.GAgentService.Projection.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGAgentServiceProjection(
        this IServiceCollection services,
        Action<ServiceProjectionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new ServiceProjectionOptions();
        configure?.Invoke(options);
        services.Replace(ServiceDescriptor.Singleton(options));
        services.AddProjectionReadModelRuntime();
        services.TryAddSingleton<IProjectionClock, SystemProjectionClock>();

        services.AddServiceProjectionRuntime<ServiceCatalogProjectionContext, ProjectionMaterializationScopeGAgent<ServiceCatalogProjectionContext>>(
            static scopeKey => new ServiceCatalogProjectionContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            static context => new ServiceProjectionRuntimeLease<ServiceCatalogProjectionContext>(context.RootActorId, context));
        services.AddServiceProjectionRuntime<ServiceDeploymentCatalogProjectionContext, ProjectionMaterializationScopeGAgent<ServiceDeploymentCatalogProjectionContext>>(
            static scopeKey => new ServiceDeploymentCatalogProjectionContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            static context => new ServiceProjectionRuntimeLease<ServiceDeploymentCatalogProjectionContext>(context.RootActorId, context));
        services.AddServiceProjectionRuntime<ServiceRevisionCatalogProjectionContext, ProjectionMaterializationScopeGAgent<ServiceRevisionCatalogProjectionContext>>(
            static scopeKey => new ServiceRevisionCatalogProjectionContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            static context => new ServiceProjectionRuntimeLease<ServiceRevisionCatalogProjectionContext>(context.RootActorId, context));
        services.AddServiceProjectionRuntime<ServiceServingSetProjectionContext, ProjectionMaterializationScopeGAgent<ServiceServingSetProjectionContext>>(
            static scopeKey => new ServiceServingSetProjectionContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            static context => new ServiceProjectionRuntimeLease<ServiceServingSetProjectionContext>(context.RootActorId, context));
        services.AddServiceProjectionRuntime<ServiceRolloutProjectionContext, ProjectionMaterializationScopeGAgent<ServiceRolloutProjectionContext>>(
            static scopeKey => new ServiceRolloutProjectionContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            static context => new ServiceProjectionRuntimeLease<ServiceRolloutProjectionContext>(context.RootActorId, context));
        services.AddServiceProjectionRuntime<ServiceTrafficViewProjectionContext, ProjectionMaterializationScopeGAgent<ServiceTrafficViewProjectionContext>>(
            static scopeKey => new ServiceTrafficViewProjectionContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            static context => new ServiceProjectionRuntimeLease<ServiceTrafficViewProjectionContext>(context.RootActorId, context));
        services.AddServiceProjectionRuntime<ServiceRunCurrentStateProjectionContext, ProjectionMaterializationScopeGAgent<ServiceRunCurrentStateProjectionContext>>(
            static scopeKey => new ServiceRunCurrentStateProjectionContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            static context => new ServiceProjectionRuntimeLease<ServiceRunCurrentStateProjectionContext>(context.RootActorId, context));
        services.AddServiceProjectionRuntime<ServiceInvocationCatalogProjectionContext, ProjectionMaterializationScopeGAgent<ServiceInvocationCatalogProjectionContext>>(
            static scopeKey => new ServiceInvocationCatalogProjectionContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            static context => new ServiceProjectionRuntimeLease<ServiceInvocationCatalogProjectionContext>(context.RootActorId, context));
        services.AddServiceProjectionRuntime<GAgentRunTerminalProjectionContext, ProjectionMaterializationScopeGAgent<GAgentRunTerminalProjectionContext>>(
            static scopeKey => new GAgentRunTerminalProjectionContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
                CorrelationId = scopeKey.SessionId,
                InteractionKind = GAgentRunTerminalProjectionPort.ResolveInteractionKind(scopeKey.ProjectionKind),
            },
            static context => new ServiceProjectionRuntimeLease<GAgentRunTerminalProjectionContext>(context.RootActorId, context));
        services.AddServiceProjectionRuntime<LlmSessionCurrentStateProjectionContext, ProjectionMaterializationScopeGAgent<LlmSessionCurrentStateProjectionContext>>(
            static scopeKey => new LlmSessionCurrentStateProjectionContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            static context => new ServiceProjectionRuntimeLease<LlmSessionCurrentStateProjectionContext>(context.RootActorId, context));
        services.AddServiceProjectionRuntime<ResponsesAgentToolStateCurrentStateProjectionContext, ProjectionMaterializationScopeGAgent<ResponsesAgentToolStateCurrentStateProjectionContext>>(
            static scopeKey => new ResponsesAgentToolStateCurrentStateProjectionContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            static context => new ServiceProjectionRuntimeLease<ResponsesAgentToolStateCurrentStateProjectionContext>(context.RootActorId, context));
        services.AddServiceProjectionRuntime<ScheduledDispatchProjectionContext, ProjectionMaterializationScopeGAgent<ScheduledDispatchProjectionContext>>(
            static scopeKey => new ScheduledDispatchProjectionContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            static context => new ServiceProjectionRuntimeLease<ScheduledDispatchProjectionContext>(context.RootActorId, context));
        services.AddEventSinkProjectionRuntimeCore<
            GAgentDraftRunProjectionContext,
            GAgentDraftRunRuntimeLease,
            AGUIEvent,
            ProjectionSessionScopeGAgent<GAgentDraftRunProjectionContext>>(
            static scopeKey => new GAgentDraftRunProjectionContext
            {
                SessionId = scopeKey.SessionId,
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            static context => new GAgentDraftRunRuntimeLease(context));
        services.AddEventSinkProjectionRuntimeCore<
            ScriptServiceAguiProjectionContext,
            ScriptServiceAguiRuntimeLease,
            AGUIEvent,
            ProjectionSessionScopeGAgent<ScriptServiceAguiProjectionContext>>(
            static scopeKey => new ScriptServiceAguiProjectionContext
            {
                SessionId = scopeKey.SessionId,
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            static context => new ScriptServiceAguiRuntimeLease(context));
        services.AddEventSinkProjectionRuntimeCore<
            LlmSessionObservationProjectionContext,
            LlmSessionObservationRuntimeLease,
            EventEnvelope,
            ProjectionSessionScopeGAgent<LlmSessionObservationProjectionContext>>(
            static scopeKey => new LlmSessionObservationProjectionContext
            {
                SessionId = scopeKey.SessionId,
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            static context => new LlmSessionObservationRuntimeLease(context));

        services.TryAddSingleton<IGAgentRunTerminalProjectionPort, GAgentRunTerminalProjectionPort>();
        services.TryAddSingleton<IGAgentDraftRunObservationScopeLeasePreparationPort, GAgentDraftRunObservationScopeLeasePreparationPort>();
        services.TryAddSingleton<ILlmSessionObservationScopeLeasePreparationPort, LlmSessionObservationScopeLeasePreparationPort>();
        services.TryAddSingleton<IProjectionSessionEventCodec<AGUIEvent>, GAgentDraftRunSessionEventCodec>();
        services.TryAddSingleton<IProjectionSessionEventHub<AGUIEvent>, ProjectionSessionEventHub<AGUIEvent>>();
        services.TryAddSingleton<LlmSessionObservationSessionEventCodec>();
        services.TryAddSingleton<LlmSessionObservationSessionEventHub>();
        services.TryAddSingleton<IGAgentDraftRunProjectionPort, GAgentDraftRunProjectionPort>();
        services.TryAddSingleton<IScriptServiceAguiProjectionPort, ScriptServiceAguiProjectionPort>();
        services.TryAddSingleton<ILlmSessionObservationProjectionPort, LlmSessionObservationProjectionPort>();
        services.TryAddSingleton<ProjectionActivationPlanDispatcher>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            ICommittedStatePublicationHook,
            CommittedStateProjectionActivationHook>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionActivationPlanProvider,
            ServiceCommittedStateProjectionActivationPlanProvider>());
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ServiceCatalogReadModel>, ServiceCatalogReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ServiceDeploymentCatalogReadModel>, ServiceDeploymentCatalogReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ServiceServingSetReadModel>, ServiceServingSetReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ServiceRolloutReadModel>, ServiceRolloutReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ServiceRolloutCommandObservationReadModel>, ServiceRolloutCommandObservationReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ServiceTrafficViewReadModel>, ServiceTrafficViewReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ServiceRevisionCatalogReadModel>, ServiceRevisionCatalogReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ServiceInvocationCatalogReadModel>, ServiceInvocationCatalogReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ServiceRunCurrentStateReadModel>, ServiceRunCurrentStateReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<GAgentRunTerminalReadModel>, GAgentRunTerminalReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<LlmSessionCurrentStateReadModel>, LlmSessionCurrentStateReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ResponsesAgentToolStateCurrentStateReadModel>, ResponsesAgentToolStateCurrentStateReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ScheduledDispatchDocument>, ScheduledDispatchDocumentMetadataProvider>();
        services.TryAddSingleton<IServiceCatalogQueryReader, ServiceCatalogQueryReader>();
        services.TryAddSingleton<IServiceDeploymentCatalogQueryReader, ServiceDeploymentCatalogQueryReader>();
        services.TryAddSingleton<IServiceServingSetQueryReader, ServiceServingSetQueryReader>();
        services.TryAddSingleton<IServiceRolloutQueryReader, ServiceRolloutQueryReader>();
        services.TryAddSingleton<IServiceRolloutCommandObservationQueryReader, ServiceRolloutCommandObservationQueryReader>();
        services.TryAddSingleton<IServiceTrafficViewQueryReader, ServiceTrafficViewQueryReader>();
        services.TryAddSingleton<IServiceRevisionCatalogQueryReader, ServiceRevisionCatalogQueryReader>();
        services.TryAddSingleton<IServiceInvocationCatalogQueryReader, ServiceInvocationCatalogQueryReader>();
        services.TryAddSingleton<IServiceScriptingRepublishCandidateQueryReader, ServiceScriptingRepublishCandidateQueryReader>();
        services.TryAddSingleton<IServiceRunQueryPort, ServiceRunQueryReader>();
        services.TryAddSingleton<IGAgentRunTerminalQueryPort, GAgentRunTerminalQueryReader>();
        services.TryAddSingleton<ILlmSessionQueryPort, LlmSessionQueryReader>();
        services.TryAddSingleton<IResponsesAgentToolStateQueryPort, ResponsesAgentToolStateQueryReader>();
        services.TryAddSingleton<ScheduledDispatchQueryPort>();
        services.TryAddSingleton<IScheduledDispatchQueryPort>(sp => sp.GetRequiredService<ScheduledDispatchQueryPort>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, ServiceRegistrationSucceededAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, ServiceRegistrationFailedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, ServiceRegistrationRetiredAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, ServiceRevisionPublishedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, ServiceDeploymentActivatedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, ServiceDeploymentDeactivatedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, ScheduledDispatchConfiguredAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, ScheduledDispatchEnabledAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, ScheduledDispatchDisabledAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, ScheduledDispatchDeletedAuditTranslator>());
        services.AddProjectionArtifactMaterializer<
            ServiceCatalogProjectionContext,
            ServiceCatalogProjector>();
        services.AddAuditCommittedFactMaterializer<ServiceCatalogProjectionContext>();
        services.AddProjectionArtifactMaterializer<
            ServiceDeploymentCatalogProjectionContext,
            ServiceDeploymentCatalogProjector>();
        services.AddAuditCommittedFactMaterializer<ServiceDeploymentCatalogProjectionContext>();
        services.AddCurrentStateProjectionMaterializer<
            ServiceServingSetProjectionContext,
            ServiceServingSetProjector>();
        services.AddProjectionArtifactMaterializer<
            ServiceRolloutProjectionContext,
            ServiceRolloutProjector>();
        services.AddProjectionArtifactMaterializer<
            ServiceRolloutProjectionContext,
            ServiceRolloutCommandObservationProjector>();
        services.AddCurrentStateProjectionMaterializer<
            ServiceTrafficViewProjectionContext,
            ServiceTrafficViewProjector>();
        services.AddProjectionArtifactMaterializer<
            ServiceRevisionCatalogProjectionContext,
            ServiceRevisionCatalogProjector>();
        services.AddAuditCommittedFactMaterializer<ServiceRevisionCatalogProjectionContext>();
        services.AddProjectionArtifactMaterializer<
            ServiceInvocationCatalogProjectionContext,
            ServiceInvocationCatalogProjector>();
        services.AddCurrentStateProjectionMaterializer<
            ServiceRunCurrentStateProjectionContext,
            ServiceRunCurrentStateProjector>();
        services.AddCurrentStateProjectionMaterializer<
            GAgentRunTerminalProjectionContext,
            GAgentRunTerminalProjector>();
        services.AddCurrentStateProjectionMaterializer<
            LlmSessionCurrentStateProjectionContext,
            LlmSessionCurrentStateProjector>();
        services.AddCurrentStateProjectionMaterializer<
            ResponsesAgentToolStateCurrentStateProjectionContext,
            ResponsesAgentToolStateCurrentStateProjector>();
        services.AddCurrentStateProjectionMaterializer<
            ScheduledDispatchProjectionContext,
            ScheduledDispatchCurrentStateProjector>();
        services.AddAuditCommittedFactMaterializer<ScheduledDispatchProjectionContext>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionProjector<GAgentDraftRunProjectionContext>,
            GAgentDraftRunSessionEventProjector>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionProjector<ScriptServiceAguiProjectionContext>,
            ScriptServiceAguiSessionEventProjector>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionProjector<LlmSessionObservationProjectionContext>,
            LlmSessionObservationSessionEventProjector>());

        return services;
    }

    private static IServiceCollection AddServiceProjectionRuntime<TContext, TScopeAgent>(
        this IServiceCollection services,
        Func<ProjectionRuntimeScopeKey, TContext> contextFactory,
        Func<TContext, ServiceProjectionRuntimeLease<TContext>> leaseFactory)
        where TContext : class, IProjectionMaterializationContext
        where TScopeAgent : IAgent
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(leaseFactory);

        services.AddProjectionMaterializationRuntimeCore<
            TContext,
            ServiceProjectionRuntimeLease<TContext>,
            TScopeAgent>(
            contextFactory,
            leaseFactory);
        return services;
    }
}
