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
    public static IServiceCollection AddChatProjectionCqrs(
        this IServiceCollection services,
        Action<ChatProjectionOptions>? configure = null)
    {
        var options = new ChatProjectionOptions();
        configure?.Invoke(options);
        services.Replace(ServiceDescriptor.Singleton(options));

        services.TryAddSingleton<IChatRunReadModelStore, InMemoryChatRunReadModelStore>();
        RegisterFromAssembly(services, typeof(ServiceCollectionExtensions).Assembly);
        services.TryAddSingleton<IChatProjectionCoordinator, ChatProjectionCoordinator>();
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
        return services;
    }

    /// <summary>
    /// Registers a custom projector from another assembly/module.
    /// </summary>
    public static IServiceCollection AddChatProjectionProjector<TProjector>(this IServiceCollection services)
        where TProjector : class, IChatRunProjector
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IChatRunProjector, TProjector>());
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
            services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(IChatRunEventReducer), reducerType));

        var projectorTypes = GetLoadableTypes(assembly)
            .Where(x =>
                x is { IsClass: true, IsAbstract: false } &&
                !x.ContainsGenericParameters &&
                (x.IsPublic || x.IsNestedPublic) &&
                typeof(IChatRunProjector).IsAssignableFrom(x))
            .OrderBy(x => x.FullName, StringComparer.Ordinal)
            .ToList();

        foreach (var projectorType in projectorTypes)
            services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(IChatRunProjector), projectorType));
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
