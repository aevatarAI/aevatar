using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Core.Streaming;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Evolution;
using Aevatar.Scripting.Projection.Configuration;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.Projectors;
using Aevatar.Scripting.Projection.ReadModels;
using Aevatar.Scripting.Projection.Reducers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Scripting.Projection.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddScriptingProjectionComponents(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(new ScriptEvolutionProjectionOptions());
        services.TryAddSingleton(typeof(IActorStreamSubscriptionHub<>), typeof(ActorStreamSubscriptionHub<>));
        services.TryAddSingleton<IProjectionSessionEventCodec<ScriptEvolutionSessionCompletedEvent>, ScriptEvolutionSessionEventCodec>();
        services.TryAddSingleton<IProjectionSessionEventHub<ScriptEvolutionSessionCompletedEvent>, ProjectionSessionEventHub<ScriptEvolutionSessionCompletedEvent>>();

        services.TryAddSingleton<IProjectionCoordinator<ScriptEvolutionSessionProjectionContext, IReadOnlyList<string>>,
            ProjectionCoordinator<ScriptEvolutionSessionProjectionContext, IReadOnlyList<string>>>();
        services.TryAddSingleton<IProjectionDispatcher<ScriptEvolutionSessionProjectionContext>,
            ProjectionDispatcher<ScriptEvolutionSessionProjectionContext, IReadOnlyList<string>>>();
        services.TryAddSingleton<IProjectionSubscriptionRegistry<ScriptEvolutionSessionProjectionContext>,
            ProjectionSubscriptionRegistry<ScriptEvolutionSessionProjectionContext>>();
        services.TryAddSingleton<IProjectionLifecycleService<ScriptEvolutionSessionProjectionContext, IReadOnlyList<string>>,
            ProjectionLifecycleService<ScriptEvolutionSessionProjectionContext, IReadOnlyList<string>>>();
        services.TryAddSingleton<IProjectionPortActivationService<ScriptEvolutionRuntimeLease>, ScriptEvolutionProjectionActivationService>();
        services.TryAddSingleton<IProjectionPortReleaseService<ScriptEvolutionRuntimeLease>, ScriptEvolutionProjectionReleaseService>();
        services.TryAddSingleton<IProjectionPortSinkSubscriptionManager<
            ScriptEvolutionRuntimeLease,
            IScriptEvolutionEventSink,
            ScriptEvolutionSessionCompletedEvent>, ScriptEvolutionProjectionSinkSubscriptionManager>();
        services.TryAddSingleton<IProjectionPortLiveSinkForwarder<
            ScriptEvolutionRuntimeLease,
            IScriptEvolutionEventSink,
            ScriptEvolutionSessionCompletedEvent>, ScriptEvolutionProjectionLiveSinkForwarder>();
        services.TryAddSingleton<ScriptEvolutionProjectionLifecycleService>();
        services.TryAddSingleton<IScriptEvolutionProjectionLifecyclePort>(sp =>
            sp.GetRequiredService<ScriptEvolutionProjectionLifecycleService>());

        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionEventReducer<ScriptExecutionReadModel, ScriptProjectionContext>,
            ScriptRunDomainEventCommittedReducer>());

        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionEventReducer<ScriptEvolutionReadModel, ScriptEvolutionProjectionContext>,
            ScriptEvolutionProposedEventReducer>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionEventReducer<ScriptEvolutionReadModel, ScriptEvolutionProjectionContext>,
            ScriptEvolutionValidatedEventReducer>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionEventReducer<ScriptEvolutionReadModel, ScriptEvolutionProjectionContext>,
            ScriptEvolutionRejectedEventReducer>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionEventReducer<ScriptEvolutionReadModel, ScriptEvolutionProjectionContext>,
            ScriptEvolutionPromotedEventReducer>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionEventReducer<ScriptEvolutionReadModel, ScriptEvolutionProjectionContext>,
            ScriptEvolutionRolledBackEventReducer>());

        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionProjector<ScriptProjectionContext, IReadOnlyList<string>>,
            ScriptExecutionReadModelProjector>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionProjector<ScriptEvolutionProjectionContext, IReadOnlyList<string>>,
            ScriptEvolutionReadModelProjector>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionProjector<ScriptEvolutionSessionProjectionContext, IReadOnlyList<string>>,
            ScriptEvolutionSessionCompletedEventProjector>());

        return services;
    }
}
