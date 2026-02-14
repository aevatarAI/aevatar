using Aevatar.CQRS.Projections.Configuration;
using Aevatar.CQRS.Projections.Orchestration;
using Aevatar.CQRS.Projections.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Reflection;

namespace Aevatar.CQRS.Projections.DependencyInjection;

/// <summary>
/// DI registration for chat CQRS projection pipeline.
/// </summary>
public static class ServiceCollectionExtensions
{
    private static readonly Type ProjectionReducerContract = typeof(IProjectionEventReducer<,>);
    private static readonly Type ProjectionProjectorContract = typeof(IProjectionProjector<,>);

    public static IServiceCollection AddWorkflowExecutionProjectionCQRS(
        this IServiceCollection services,
        Action<WorkflowExecutionProjectionOptions>? configure = null)
    {
        var options = new WorkflowExecutionProjectionOptions();
        configure?.Invoke(options);
        services.Replace(ServiceDescriptor.Singleton(options));

        services.TryAddSingleton<IWorkflowExecutionReadModelStore, InMemoryWorkflowExecutionReadModelStore>();
        services.TryAddSingleton<IProjectionReadModelStore<WorkflowExecutionReport, string>>(sp =>
            sp.GetRequiredService<IWorkflowExecutionReadModelStore>());
        RegisterFromAssembly(services, typeof(ServiceCollectionExtensions).Assembly);
        services.TryAddSingleton<IProjectionCoordinator<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>, WorkflowExecutionProjectionCoordinator>();
        services.TryAddSingleton<IWorkflowExecutionProjectionCoordinator>(sp =>
            (IWorkflowExecutionProjectionCoordinator)sp.GetRequiredService<IProjectionCoordinator<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>>());
        services.TryAddSingleton<IWorkflowExecutionProjectionSubscriptionRegistry, WorkflowExecutionProjectionSubscriptionRegistry>();
        services.TryAddSingleton<IWorkflowExecutionProjectionService, WorkflowExecutionProjectionService>();
        return services;
    }

    /// <summary>
    /// Registers a custom reducer from another assembly/module.
    /// </summary>
    public static IServiceCollection AddWorkflowExecutionProjectionReducer<TReducer>(this IServiceCollection services)
        where TReducer : class, IWorkflowExecutionEventReducer
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkflowExecutionEventReducer, TReducer>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton(
            typeof(IProjectionEventReducer<WorkflowExecutionReport, WorkflowExecutionProjectionContext>),
            typeof(TReducer)));
        return services;
    }

    /// <summary>
    /// Registers a custom projector from another assembly/module.
    /// </summary>
    public static IServiceCollection AddWorkflowExecutionProjectionProjector<TProjector>(this IServiceCollection services)
        where TProjector : class, IWorkflowExecutionProjector
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkflowExecutionProjector, TProjector>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton(
            typeof(IProjectionProjector<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>),
            typeof(TProjector)));
        return services;
    }

    /// <summary>
    /// Registers all reducer/projector implementations from an extension assembly.
    /// </summary>
    public static IServiceCollection AddWorkflowExecutionProjectionExtensionsFromAssembly(
        this IServiceCollection services,
        Assembly assembly)
    {
        RegisterFromAssembly(services, assembly);
        return services;
    }

    /// <summary>
    /// Replaces the default read-model store implementation.
    /// </summary>
    public static IServiceCollection AddWorkflowExecutionProjectionReadModelStore<TStore>(this IServiceCollection services)
        where TStore : class, IWorkflowExecutionReadModelStore
    {
        services.Replace(ServiceDescriptor.Singleton<IWorkflowExecutionReadModelStore, TStore>());
        services.Replace(ServiceDescriptor.Singleton<IProjectionReadModelStore<WorkflowExecutionReport, string>>(sp =>
            sp.GetRequiredService<IWorkflowExecutionReadModelStore>()));
        return services;
    }

    private static void RegisterFromAssembly(IServiceCollection services, Assembly assembly)
    {
        var reducerTypes = GetLoadableTypes(assembly)
            .Where(x =>
                x is { IsClass: true, IsAbstract: false } &&
                !x.ContainsGenericParameters &&
                (x.IsPublic || x.IsNestedPublic) &&
                typeof(IWorkflowExecutionEventReducer).IsAssignableFrom(x))
            .OrderBy(x => x.FullName, StringComparer.Ordinal)
            .ToList();

        foreach (var reducerType in reducerTypes)
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(IWorkflowExecutionEventReducer), reducerType));
            RegisterProjectionContracts(services, reducerType, ProjectionReducerContract);
        }

        var projectorTypes = GetLoadableTypes(assembly)
            .Where(x =>
                x is { IsClass: true, IsAbstract: false } &&
                !x.ContainsGenericParameters &&
                (x.IsPublic || x.IsNestedPublic) &&
                typeof(IWorkflowExecutionProjector).IsAssignableFrom(x))
            .OrderBy(x => x.FullName, StringComparer.Ordinal)
            .ToList();

        foreach (var projectorType in projectorTypes)
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(IWorkflowExecutionProjector), projectorType));
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
