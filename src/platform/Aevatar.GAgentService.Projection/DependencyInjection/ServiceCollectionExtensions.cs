using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Core.Streaming;
using Aevatar.CQRS.Projection.Runtime.DependencyInjection;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Projection.Configuration;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.Metadata;
using Aevatar.GAgentService.Projection.Orchestration;
using Aevatar.GAgentService.Projection.Projectors;
using Aevatar.GAgentService.Projection.Queries;
using Aevatar.GAgentService.Projection.ReadModels;
using Aevatar.Presentation.AGUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

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

        services.TryAddSingleton<IServiceCatalogProjectionPort, ServiceCatalogProjectionPort>();
        services.TryAddSingleton<IServiceDeploymentCatalogProjectionPort, ServiceDeploymentCatalogProjectionPort>();
        services.TryAddSingleton<IServiceServingSetProjectionPort, ServiceServingSetProjectionPort>();
        services.TryAddSingleton<IServiceRolloutProjectionPort, ServiceRolloutProjectionPort>();
        services.TryAddSingleton<IServiceTrafficViewProjectionPort, ServiceTrafficViewProjectionPort>();
        services.TryAddSingleton<IServiceRevisionCatalogProjectionPort, ServiceRevisionCatalogProjectionPort>();
        services.TryAddSingleton<IServiceRunCurrentStateProjectionPort, ServiceRunCurrentStateProjectionPort>();
        services.TryAddSingleton<IGAgentRunTerminalProjectionPort, GAgentRunTerminalProjectionPort>();
        services.TryAddSingleton<ILlmSessionCurrentStateProjectionPort, LlmSessionCurrentStateProjectionPort>();
        services.TryAddSingleton<IResponsesAgentToolStateCurrentStateProjectionPort, ResponsesAgentToolStateCurrentStateProjectionPort>();
        // Fix (pr678-review): see Aevatar.GAgents.NyxidChat ServiceCollectionExtensions.
        //   AGUIEvent is projected by three pipelines (draft-run, script-service, NyxId chat);
        //   a single shared IProjectionSessionEventHub<AGUIEvent> made TryAddSingleton silently
        //   drop all codecs but the first. Each pipeline now builds its own channel-scoped hub.
        services.TryAddSingleton<IGAgentDraftRunProjectionPort>(static sp =>
            new GAgentDraftRunProjectionPort(
                sp.GetRequiredService<ServiceProjectionOptions>(),
                sp.GetRequiredService<IProjectionScopeActivationService<GAgentDraftRunRuntimeLease>>(),
                sp.GetRequiredService<IProjectionScopeReleaseService<GAgentDraftRunRuntimeLease>>(),
                CreateAguiSessionEventHub(sp, new GAgentDraftRunSessionEventCodec())));
        services.TryAddSingleton<IScriptServiceAguiProjectionPort>(static sp =>
            new ScriptServiceAguiProjectionPort(
                sp.GetRequiredService<ServiceProjectionOptions>(),
                sp.GetRequiredService<IProjectionScopeActivationService<ScriptServiceAguiRuntimeLease>>(),
                sp.GetRequiredService<IProjectionScopeReleaseService<ScriptServiceAguiRuntimeLease>>(),
                CreateAguiSessionEventHub(sp, new ScriptServiceAguiSessionEventCodec())));
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ServiceCatalogReadModel>, ServiceCatalogReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ServiceDeploymentCatalogReadModel>, ServiceDeploymentCatalogReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ServiceServingSetReadModel>, ServiceServingSetReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ServiceRolloutReadModel>, ServiceRolloutReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ServiceRolloutCommandObservationReadModel>, ServiceRolloutCommandObservationReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ServiceTrafficViewReadModel>, ServiceTrafficViewReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ServiceRevisionCatalogReadModel>, ServiceRevisionCatalogReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ServiceRunCurrentStateReadModel>, ServiceRunCurrentStateReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<GAgentRunTerminalReadModel>, GAgentRunTerminalReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<LlmSessionCurrentStateReadModel>, LlmSessionCurrentStateReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ResponsesAgentToolStateCurrentStateReadModel>, ResponsesAgentToolStateCurrentStateReadModelMetadataProvider>();
        services.TryAddSingleton<IServiceCatalogQueryReader, ServiceCatalogQueryReader>();
        services.TryAddSingleton<IServiceDeploymentCatalogQueryReader, ServiceDeploymentCatalogQueryReader>();
        services.TryAddSingleton<IServiceServingSetQueryReader, ServiceServingSetQueryReader>();
        services.TryAddSingleton<IServiceRolloutQueryReader, ServiceRolloutQueryReader>();
        services.TryAddSingleton<IServiceRolloutCommandObservationQueryReader, ServiceRolloutCommandObservationQueryReader>();
        services.TryAddSingleton<IServiceTrafficViewQueryReader, ServiceTrafficViewQueryReader>();
        services.TryAddSingleton<IServiceRevisionCatalogQueryReader, ServiceRevisionCatalogQueryReader>();
        services.TryAddSingleton<IServiceRunQueryPort, ServiceRunQueryReader>();
        services.TryAddSingleton<IGAgentRunTerminalQueryPort, GAgentRunTerminalQueryReader>();
        services.TryAddSingleton<ILlmSessionQueryPort, LlmSessionQueryReader>();
        services.TryAddSingleton<IResponsesAgentToolStateQueryPort, ResponsesAgentToolStateQueryReader>();
        services.AddProjectionArtifactMaterializer<
            ServiceCatalogProjectionContext,
            ServiceCatalogProjector>();
        services.AddProjectionArtifactMaterializer<
            ServiceDeploymentCatalogProjectionContext,
            ServiceDeploymentCatalogProjector>();
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
        services.TryAddEnumerable(ServiceDescriptor
            .Singleton<IProjectionProjector<GAgentDraftRunProjectionContext>, GAgentDraftRunSessionEventProjector>(
                static sp => new GAgentDraftRunSessionEventProjector(
                    CreateAguiSessionEventHub(sp, new GAgentDraftRunSessionEventCodec()))));
        services.TryAddEnumerable(ServiceDescriptor
            .Singleton<IProjectionProjector<ScriptServiceAguiProjectionContext>, ScriptServiceAguiSessionEventProjector>(
                static sp => new ScriptServiceAguiSessionEventProjector(
                    CreateAguiSessionEventHub(sp, new ScriptServiceAguiSessionEventCodec()))));

        return services;
    }

    // Fix (pr678-review): builds a channel-scoped AGUIEvent hub for one projection
    //   pipeline. The hub holds no mutable state, so a per-consumer instance is safe.
    private static ProjectionSessionEventHub<AGUIEvent> CreateAguiSessionEventHub(
        IServiceProvider sp,
        IProjectionSessionEventCodec<AGUIEvent> codec) =>
        new(
            sp.GetRequiredService<IStreamProvider>(),
            codec,
            sp.GetService<ILogger<ProjectionSessionEventHub<AGUIEvent>>>());

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
