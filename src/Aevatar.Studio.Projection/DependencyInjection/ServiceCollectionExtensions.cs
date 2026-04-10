using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Projection.CommandServices;
using Aevatar.Studio.Projection.Metadata;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Projection.Projectors;
using Aevatar.Studio.Projection.QueryPorts;
using Aevatar.Studio.Projection.ReadModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Studio.Projection.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Studio projection components: materialization runtime,
    /// projectors, query ports, command services, and document metadata providers.
    /// </summary>
    public static IServiceCollection AddStudioProjectionComponents(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

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

        // UserConfig projector
        services.AddCurrentStateProjectionMaterializer<
            StudioMaterializationContext,
            UserConfigCurrentStateProjector>();

        // Document metadata providers (for index creation in Elasticsearch)
        services.TryAddSingleton<
            IProjectionDocumentMetadataProvider<UserConfigCurrentStateDocument>,
            UserConfigCurrentStateDocumentMetadataProvider>();

        // Query ports (read side)
        services.TryAddSingleton<IUserConfigQueryPort, ProjectionUserConfigQueryPort>();

        // Command services (write side)
        services.TryAddSingleton<IUserConfigCommandService, ActorDispatchUserConfigCommandService>();

        return services;
    }
}
