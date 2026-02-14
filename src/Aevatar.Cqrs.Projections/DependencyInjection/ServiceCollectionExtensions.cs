using Aevatar.Cqrs.Projections.Configuration;
using Aevatar.Cqrs.Projections.Orchestration;
using Aevatar.Cqrs.Projections.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Reflection;

namespace Aevatar.Cqrs.Projections.DependencyInjection;

/// <summary>
/// DI registration for chat CQRS projection pipeline.
/// </summary>
public static class ServiceCollectionExtensions
{
    private static readonly Type ProjectionReducerContract = typeof(IProjectionEventReducer<,>);
    private static readonly Type ProjectionProjectorContract = typeof(IProjectionProjector<,>);

    public static IServiceCollection AddChatProjectionCqrs(
        this IServiceCollection services,
        Action<ChatProjectionOptions>? configure = null)
    {
        var options = new ChatProjectionOptions();
        configure?.Invoke(options);
        services.Replace(ServiceDescriptor.Singleton(options));

        services.TryAddSingleton<IChatRunReadModelStore, InMemoryChatRunReadModelStore>();
        services.TryAddSingleton<IProjectionReadModelStore<ChatRunReport, string>>(sp =>
            sp.GetRequiredService<IChatRunReadModelStore>());
        RegisterFromAssembly(services, typeof(ServiceCollectionExtensions).Assembly);
        services.TryAddSingleton<IProjectionCoordinator<ChatProjectionContext, IReadOnlyList<ChatTopologyEdge>>, ChatProjectionCoordinator>();
        services.TryAddSingleton<IChatProjectionCoordinator>(sp =>
            (IChatProjectionCoordinator)sp.GetRequiredService<IProjectionCoordinator<ChatProjectionContext, IReadOnlyList<ChatTopologyEdge>>>());
        services.TryAddSingleton<IChatProjectionSubscriptionRegistry, ChatProjectionSubscriptionRegistry>();
        services.TryAddSingleton<IChatRunProjectionService, ChatRunProjectionService>();
        return services;
    }

    /// <summary>
    /// Registers a custom reducer from another assembly/module.
    /// </summary>
    public static IServiceCollection AddChatProjectionReducer<TReducer>(this IServiceCollection services)
        where TReducer : class, IChatRunEventReducer
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IChatRunEventReducer, TReducer>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton(
            typeof(IProjectionEventReducer<ChatRunReport, ChatProjectionContext>),
            typeof(TReducer)));
        return services;
    }

    /// <summary>
    /// Registers a custom projector from another assembly/module.
    /// </summary>
    public static IServiceCollection AddChatProjectionProjector<TProjector>(this IServiceCollection services)
        where TProjector : class, IChatRunProjector
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IChatRunProjector, TProjector>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton(
            typeof(IProjectionProjector<ChatProjectionContext, IReadOnlyList<ChatTopologyEdge>>),
            typeof(TProjector)));
        return services;
    }

    /// <summary>
    /// Registers all reducer/projector implementations from an extension assembly.
    /// </summary>
    public static IServiceCollection AddChatProjectionExtensionsFromAssembly(
        this IServiceCollection services,
        Assembly assembly)
    {
        RegisterFromAssembly(services, assembly);
        return services;
    }

    /// <summary>
    /// Replaces the default read-model store implementation.
    /// </summary>
    public static IServiceCollection AddChatProjectionReadModelStore<TStore>(this IServiceCollection services)
        where TStore : class, IChatRunReadModelStore
    {
        services.Replace(ServiceDescriptor.Singleton<IChatRunReadModelStore, TStore>());
        services.Replace(ServiceDescriptor.Singleton<IProjectionReadModelStore<ChatRunReport, string>>(sp =>
            sp.GetRequiredService<IChatRunReadModelStore>()));
        return services;
    }

    private static void RegisterFromAssembly(IServiceCollection services, Assembly assembly)
    {
        var reducerTypes = GetLoadableTypes(assembly)
            .Where(x =>
                x is { IsClass: true, IsAbstract: false } &&
                !x.ContainsGenericParameters &&
                (x.IsPublic || x.IsNestedPublic) &&
                typeof(IChatRunEventReducer).IsAssignableFrom(x))
            .OrderBy(x => x.FullName, StringComparer.Ordinal)
            .ToList();

        foreach (var reducerType in reducerTypes)
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(IChatRunEventReducer), reducerType));
            RegisterProjectionContracts(services, reducerType, ProjectionReducerContract);
        }

        var projectorTypes = GetLoadableTypes(assembly)
            .Where(x =>
                x is { IsClass: true, IsAbstract: false } &&
                !x.ContainsGenericParameters &&
                (x.IsPublic || x.IsNestedPublic) &&
                typeof(IChatRunProjector).IsAssignableFrom(x))
            .OrderBy(x => x.FullName, StringComparer.Ordinal)
            .ToList();

        foreach (var projectorType in projectorTypes)
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(IChatRunProjector), projectorType));
            RegisterProjectionContracts(services, projectorType, ProjectionProjectorContract);
        }
    }

    private static void RegisterProjectionContracts(
        IServiceCollection services,
        Type implementationType,
        Type genericContract)
    {
        var contractTypes = implementationType.GetInterfaces()
            .Where(x =>
                x.IsGenericType &&
                x.GetGenericTypeDefinition() == genericContract)
            .Distinct()
            .ToList();

        foreach (var contractType in contractTypes)
            services.TryAddEnumerable(ServiceDescriptor.Singleton(contractType, implementationType));
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(x => x != null)!;
        }
    }
}
