using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.CQRS.Core.DependencyInjection;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Projection.CommandServices;
using Aevatar.Studio.Projection.Metadata;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Projection.Projectors;
using Aevatar.Studio.Projection.QueryPorts;
using Aevatar.Studio.Projection.ReadModels;
using Aevatar.GAgents.StudioMember;
using Aevatar.Studio.Workspace;
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

        // ── Projectors ──

        services.AddCurrentStateProjectionMaterializer<
            StudioMaterializationContext,
            UserConfigCurrentStateProjector>();

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
            ChatHistoryIndexCurrentStateProjector>();

        services.AddCurrentStateProjectionMaterializer<
            StudioMaterializationContext,
            ChatConversationCurrentStateProjector>();

        services.AddCurrentStateProjectionMaterializer<
            StudioMaterializationContext,
            StudioMemberCurrentStateProjector>();

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

        // ── Document metadata providers (for index creation in Elasticsearch) ──

        services.TryAddSingleton<
            IProjectionDocumentMetadataProvider<UserConfigCurrentStateDocument>,
            UserConfigCurrentStateDocumentMetadataProvider>();

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
            IProjectionDocumentMetadataProvider<ChatHistoryIndexCurrentStateDocument>,
            ChatHistoryIndexCurrentStateDocumentMetadataProvider>();

        services.TryAddSingleton<
            IProjectionDocumentMetadataProvider<ChatConversationCurrentStateDocument>,
            ChatConversationCurrentStateDocumentMetadataProvider>();

        services.TryAddSingleton<
            IProjectionDocumentMetadataProvider<StudioMemberCurrentStateDocument>,
            StudioMemberCurrentStateDocumentMetadataProvider>();

        services.TryAddSingleton<
            IProjectionDocumentMetadataProvider<StudioMemberBindingRunCurrentStateDocument>,
            StudioMemberBindingRunCurrentStateDocumentMetadataProvider>();

        services.TryAddSingleton<
            IProjectionDocumentMetadataProvider<StudioTeamCurrentStateDocument>,
            StudioTeamCurrentStateDocumentMetadataProvider>();

        services.TryAddSingleton<
            IProjectionDocumentMetadataProvider<StudioWorkspaceCurrentStateDocument>,
            StudioWorkspaceCurrentStateDocumentMetadataProvider>();

        services.TryAddSingleton<ProjectionActivationPlanDispatcher>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            ICommittedStatePublicationHook,
            CommittedStateProjectionActivationHook>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionActivationPlanProvider,
            StudioCommittedStateProjectionActivationPlanProvider>());

        // Compile-time-safe actor provisioning used by every Studio actor-backed
        // store. Projection activation is driven by committed-state plans.
        services.TryAddSingleton<IStudioActorBootstrap, StudioActorBootstrap>();

        // Query ports (read side)
        services.TryAddSingleton<IUserConfigQueryPort, ProjectionUserConfigQueryPort>();
        services.TryAddSingleton<IStudioMemberQueryPort, ProjectionStudioMemberQueryPort>();
        services.TryAddSingleton<IStudioMemberBindingRunQueryPort, ProjectionStudioMemberBindingRunQueryPort>();
        services.TryAddSingleton<IStudioTeamQueryPort, ProjectionStudioTeamQueryPort>();
        services.TryAddSingleton<IStudioWorkspaceQueryPort, ProjectionStudioWorkspaceQueryPort>();

        // Command services (write side)
        services.TryAddSingleton<IUserConfigCommandService, ActorDispatchUserConfigCommandService>();
        services.TryAddSingleton<IStudioMemberCommandPort, ActorDispatchStudioMemberCommandService>();
        services.TryAddSingleton<IStudioMemberPlatformBindingCommandPort, ScopeBindingStudioMemberPlatformBindingCommandService>();
        services.TryAddSingleton<IStudioTeamCommandPort, ActorDispatchStudioTeamCommandService>();

        return services;
    }
}
