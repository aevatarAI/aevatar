using Aevatar.Demos.CaseProjections.Configuration;
using Aevatar.Demos.CaseProjections.Orchestration;
using Aevatar.Demos.CaseProjections.Stores;
using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Reflection;

namespace Aevatar.Demos.CaseProjections.DependencyInjection;

public static class ServiceCollectionExtensions
{
    private static readonly Type ReducerContract = typeof(IProjectionEventReducer<,>);
    private static readonly Type ProjectorContract = typeof(IProjectionProjector<,>);

    public static IServiceCollection AddCaseProjectionDemo(
        this IServiceCollection services,
        Action<CaseProjectionOptions>? configure = null)
    {
        var options = new CaseProjectionOptions();
        configure?.Invoke(options);
        services.Replace(ServiceDescriptor.Singleton(options));
        services.TryAddSingleton<CaseProjectionOptions>(sp =>
            sp.GetRequiredService<CaseProjectionOptions>());
        services.TryAddSingleton<InMemoryCaseReadModelStore>();
        services.TryAddSingleton<IProjectionReadModelStore<CaseProjectionReadModel, string>>(sp =>
            sp.GetRequiredService<InMemoryCaseReadModelStore>());
        services.TryAddSingleton<IProjectionRunIdGenerator, GuidProjectionRunIdGenerator>();
        services.TryAddSingleton<IProjectionClock, SystemProjectionClock>();
        services.TryAddSingleton<ICaseProjectionContextFactory, DefaultCaseProjectionContextFactory>();

        RegisterFromAssembly(services, typeof(ServiceCollectionExtensions).Assembly);

        services.TryAddSingleton<IProjectionCoordinator<CaseProjectionContext, IReadOnlyList<CaseTopologyEdge>>, ProjectionCoordinator<CaseProjectionContext, IReadOnlyList<CaseTopologyEdge>>>();
        services.TryAddSingleton<IProjectionCompletionDetector<CaseProjectionContext>, CaseResolvedProjectionCompletionDetector>();
        services.TryAddSingleton<IProjectionSubscriptionRegistry<CaseProjectionContext>, ProjectionSubscriptionRegistry<CaseProjectionContext, IReadOnlyList<CaseTopologyEdge>>>();
        services.TryAddSingleton(typeof(IActorStreamSubscriptionHub<>), typeof(ActorStreamSubscriptionHub<>));
        services.TryAddSingleton<IProjectionLifecycleService<CaseProjectionContext, IReadOnlyList<CaseTopologyEdge>>, ProjectionLifecycleService<CaseProjectionContext, IReadOnlyList<CaseTopologyEdge>>>();
        services.TryAddSingleton<ICaseProjectionService, CaseProjectionService>();

        return services;
    }

    public static IServiceCollection AddCaseProjectionReducer<TReducer>(this IServiceCollection services)
        where TReducer : class, IProjectionEventReducer<CaseProjectionReadModel, CaseProjectionContext>
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton(
            typeof(IProjectionEventReducer<CaseProjectionReadModel, CaseProjectionContext>),
            typeof(TReducer)));
        return services;
    }

    public static IServiceCollection AddCaseProjectionProjector<TProjector>(this IServiceCollection services)
        where TProjector : class, IProjectionProjector<CaseProjectionContext, IReadOnlyList<CaseTopologyEdge>>
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton(
            typeof(IProjectionProjector<CaseProjectionContext, IReadOnlyList<CaseTopologyEdge>>),
            typeof(TProjector)));
        return services;
    }

    public static IServiceCollection AddCaseProjectionExtensionsFromAssembly(
        this IServiceCollection services,
        Assembly assembly)
    {
        RegisterFromAssembly(services, assembly);
        return services;
    }

    private static void RegisterFromAssembly(IServiceCollection services, Assembly assembly)
    {
        ProjectionAssemblyRegistration.RegisterProjectionExtensionsFromAssembly(
            services,
            assembly,
            typeof(IProjectionEventReducer<CaseProjectionReadModel, CaseProjectionContext>),
            typeof(IProjectionProjector<CaseProjectionContext, IReadOnlyList<CaseTopologyEdge>>),
            ReducerContract,
            ProjectorContract);
    }
}
