using Aevatar.Foundation.Runtime.Implementations.Orleans.Actors;
using Orleans.Hosting;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAevatarFoundationRuntimeOrleans(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.Replace(ServiceDescriptor.Singleton<IActorRuntime, OrleansActorRuntime>());

        services.TryAddSingleton(typeof(IStateStore<>), typeof(InMemoryStateStore<>));
        services.TryAddSingleton<IEventStore, InMemoryEventStore>();
        services.TryAddSingleton<IAgentManifestStore, InMemoryManifestStore>();
        services.TryAddSingleton<IEventDeduplicator, MemoryCacheDeduplicator>();

        services.TryAddSingleton<IRunManager, RunManager>();
        services.TryAddSingleton<IAgentContextAccessor, AsyncLocalAgentContextAccessor>();
        services.TryAddSingleton<ICorrelationLinkPolicy, DefaultCorrelationLinkPolicy>();
        services.TryAddSingleton<IEnvelopePropagationPolicy, DefaultEnvelopePropagationPolicy>();

        return services;
    }

    public static ISiloBuilder AddAevatarFoundationRuntimeOrleans(this ISiloBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder
            .AddMemoryGrainStorage(OrleansRuntimeConstants.GrainStateStorageName)
            .ConfigureServices(services => services.AddAevatarFoundationRuntimeOrleans());
    }
}
