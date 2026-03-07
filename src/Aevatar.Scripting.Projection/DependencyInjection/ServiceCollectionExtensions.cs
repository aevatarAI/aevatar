using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Core.Streaming;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Runtime.DependencyInjection;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Evolution;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Projection.Configuration;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.Projectors;
using Aevatar.Scripting.Projection.ReadModels;
using Aevatar.Scripting.Projection.Reducers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Aevatar.Scripting.Projection.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddScriptingProjectionComponents(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(new ScriptEvolutionProjectionOptions());
        services.AddProjectionReadModelRuntime();
        services.TryAddSingleton<IProjectionDocumentStore<ScriptExecutionReadModel, string>>(provider =>
            new InMemoryProjectionDocumentStore<ScriptExecutionReadModel, string>(
                static readModel => readModel.Id,
                listSortSelector: static readModel => readModel.UpdatedAt,
                logger: provider.GetService<ILogger<InMemoryProjectionDocumentStore<ScriptExecutionReadModel, string>>>()));
        services.TryAddSingleton<IProjectionDocumentStore<ScriptEvolutionReadModel, string>>(provider =>
            new InMemoryProjectionDocumentStore<ScriptEvolutionReadModel, string>(
                static readModel => readModel.Id,
                listSortSelector: static readModel => readModel.UpdatedAt,
                logger: provider.GetService<ILogger<InMemoryProjectionDocumentStore<ScriptEvolutionReadModel, string>>>()));
        services.TryAddSingleton(typeof(IActorStreamSubscriptionHub<>), typeof(ActorStreamSubscriptionHub<>));
        services.TryAddSingleton<IProjectionSessionEventCodec<ScriptEvolutionSessionCompletedEvent>, ScriptEvolutionSessionEventCodec>();
        services.TryAddSingleton<IProjectionSessionEventHub<ScriptEvolutionSessionCompletedEvent>, ProjectionSessionEventHub<ScriptEvolutionSessionCompletedEvent>>();
        services.AddEventSinkProjectionRuntimeCore<
            ScriptEvolutionSessionProjectionContext,
            IReadOnlyList<string>,
            ScriptEvolutionRuntimeLease,
            ScriptEvolutionSessionCompletedEvent>();
        services.TryAddSingleton<IProjectionPortActivationService<ScriptEvolutionRuntimeLease>, ScriptEvolutionProjectionActivationService>();
        services.TryAddSingleton<IProjectionPortReleaseService<ScriptEvolutionRuntimeLease>, ScriptEvolutionProjectionReleaseService>();
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
