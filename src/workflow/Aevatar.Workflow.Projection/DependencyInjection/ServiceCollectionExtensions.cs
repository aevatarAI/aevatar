using Aevatar.AI.Abstractions;
using Aevatar.AI.Projection.DependencyInjection;
using Aevatar.Workflow.Projection.Configuration;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.Stores;
using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.Workflow.Projection.Appliers;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Core.Streaming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Reflection;

namespace Aevatar.Workflow.Projection.DependencyInjection;

/// <summary>
/// DI registration for chat CQRS projection pipeline.
/// </summary>
public static class ServiceCollectionExtensions
{
    private static readonly Type ProjectionReducerContract = typeof(IProjectionEventReducer<,>);
    private static readonly Type ProjectionProjectorContract = typeof(IProjectionProjector<,>);
    private static readonly Type WorkflowExecutionReducerContract = typeof(IProjectionEventReducer<WorkflowExecutionReport, WorkflowExecutionProjectionContext>);
    private static readonly Type WorkflowExecutionProjectorContract = typeof(IProjectionProjector<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>);

    public static IServiceCollection AddWorkflowExecutionProjectionCQRS(
        this IServiceCollection services,
        Action<WorkflowExecutionProjectionOptions>? configure = null)
    {
        var options = new WorkflowExecutionProjectionOptions();
        configure?.Invoke(options);
        services.Replace(ServiceDescriptor.Singleton(options));
        services.TryAddSingleton<IProjectionRuntimeOptions>(sp =>
            sp.GetRequiredService<WorkflowExecutionProjectionOptions>());

        services.TryAddSingleton<IProjectionReadModelStore<WorkflowExecutionReport, string>, InMemoryWorkflowExecutionReadModelStore>();
        services.TryAddSingleton<IProjectionClock, SystemProjectionClock>();
        services.TryAddSingleton<IWorkflowExecutionProjectionContextFactory, DefaultWorkflowExecutionProjectionContextFactory>();
        services.TryAddSingleton<WorkflowExecutionReadModelMapper>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IProjectionEventApplier<WorkflowExecutionReport, WorkflowExecutionProjectionContext, TextMessageEndEvent>, WorkflowTextMessageEndEventApplier>());
        services.AddAITextMessageEndProjectionReducer<WorkflowExecutionReport, WorkflowExecutionProjectionContext>();
        RegisterFromAssembly(services, typeof(ServiceCollectionExtensions).Assembly);
        services.TryAddSingleton<IProjectionCoordinator<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>, ProjectionCoordinator<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>>();
        services.TryAddSingleton<IProjectionDispatcher<WorkflowExecutionProjectionContext>, ProjectionDispatcher<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>>();
        services.TryAddSingleton<IProjectionSubscriptionRegistry<WorkflowExecutionProjectionContext>, ProjectionSubscriptionRegistry<WorkflowExecutionProjectionContext>>();
        services.TryAddSingleton(typeof(IActorStreamSubscriptionHub<>), typeof(ActorStreamSubscriptionHub<>));
        services.TryAddSingleton<IProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>, ProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>>();
        services.TryAddSingleton<IWorkflowExecutionProjectionPort, WorkflowExecutionProjectionService>();
        return services;
    }

    /// <summary>
    /// Registers a custom reducer from another assembly/module.
    /// </summary>
    public static IServiceCollection AddWorkflowExecutionProjectionReducer<TReducer>(this IServiceCollection services)
        where TReducer : class, IProjectionEventReducer<WorkflowExecutionReport, WorkflowExecutionProjectionContext>
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton(
            typeof(IProjectionEventReducer<WorkflowExecutionReport, WorkflowExecutionProjectionContext>),
            typeof(TReducer)));
        return services;
    }

    /// <summary>
    /// Registers a custom projector from another assembly/module.
    /// </summary>
    public static IServiceCollection AddWorkflowExecutionProjectionProjector<TProjector>(this IServiceCollection services)
        where TProjector : class, IProjectionProjector<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>
    {
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
        where TStore : class, IProjectionReadModelStore<WorkflowExecutionReport, string>
    {
        services.Replace(ServiceDescriptor.Singleton<IProjectionReadModelStore<WorkflowExecutionReport, string>, TStore>());
        return services;
    }

    private static void RegisterFromAssembly(IServiceCollection services, Assembly assembly)
    {
        ProjectionAssemblyRegistration.RegisterProjectionExtensionsFromAssembly(
            services,
            assembly,
            WorkflowExecutionReducerContract,
            WorkflowExecutionProjectorContract,
            ProjectionReducerContract,
            ProjectionProjectorContract);
    }
}
