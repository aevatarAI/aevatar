using Aevatar.Workflow.Projection.Configuration;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Foundation.Abstractions.Deduplication;
using Aevatar.CQRS.Projection.Runtime.DependencyInjection;
using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Core.Streaming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
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
        services.TryAddSingleton<IEventDeduplicator, PassthroughEventDeduplicator>();
        services.AddProjectionReadModelRuntime();
        services.TryAddSingleton<IWorkflowReadModelSelectionPlanner, WorkflowReadModelSelectionPlanner>();
        RegisterWorkflowReadModelStoreSelector(services);
        RegisterWorkflowRelationStoreSelector(services);
        services.TryAddSingleton<IProjectionClock, SystemProjectionClock>();
        services.TryAddSingleton<IWorkflowExecutionProjectionContextFactory, DefaultWorkflowExecutionProjectionContextFactory>();
        services.TryAddSingleton<WorkflowExecutionReadModelMapper>();
        RegisterFromAssembly(services, typeof(ServiceCollectionExtensions).Assembly);
        services.TryAddSingleton<IProjectionCoordinator<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>, ProjectionCoordinator<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>>();
        services.TryAddSingleton<IProjectionDispatcher<WorkflowExecutionProjectionContext>, ProjectionDispatcher<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>>();
        services.TryAddSingleton<IProjectionDispatchFailureReporter<WorkflowExecutionProjectionContext>, WorkflowProjectionDispatchFailureReporter>();
        services.TryAddSingleton<IProjectionSubscriptionRegistry<WorkflowExecutionProjectionContext>, ProjectionSubscriptionRegistry<WorkflowExecutionProjectionContext>>();
        services.TryAddSingleton(typeof(IActorStreamSubscriptionHub<>), typeof(ActorStreamSubscriptionHub<>));
        services.TryAddSingleton<IProjectionOwnershipCoordinator, ActorProjectionOwnershipCoordinator>();
        services.TryAddSingleton<IProjectionSessionEventCodec<WorkflowRunEvent>, WorkflowRunEventSessionCodec>();
        services.TryAddSingleton<IProjectionSessionEventHub<WorkflowRunEvent>, ProjectionSessionEventHub<WorkflowRunEvent>>();
        services.TryAddSingleton<IWorkflowProjectionLeaseManager, WorkflowProjectionLeaseManager>();
        services.TryAddSingleton<IWorkflowProjectionActivationService, WorkflowProjectionActivationService>();
        services.TryAddSingleton<IWorkflowProjectionReleaseService, WorkflowProjectionReleaseService>();
        services.TryAddSingleton<IWorkflowProjectionSinkSubscriptionManager, WorkflowProjectionSinkSubscriptionManager>();
        services.TryAddSingleton<IWorkflowProjectionSinkFailurePolicy, WorkflowProjectionSinkFailurePolicy>();
        services.TryAddSingleton<IWorkflowProjectionLiveSinkForwarder, WorkflowProjectionLiveSinkForwarder>();
        services.TryAddSingleton<IWorkflowProjectionReadModelUpdater, WorkflowProjectionReadModelUpdater>();
        services.TryAddSingleton<IWorkflowProjectionQueryReader, WorkflowProjectionQueryReader>();
        services.TryAddSingleton<IProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>, ProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>>();
        services.TryAddSingleton<IWorkflowExecutionProjectionPort, WorkflowExecutionProjectionService>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, WorkflowReadModelStartupValidationHostedService>());
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

    private static void RegisterWorkflowReadModelStoreSelector(IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<IProjectionReadModelStore<WorkflowExecutionReport, string>>(sp =>
        {
            var options = sp.GetRequiredService<WorkflowExecutionProjectionOptions>();
            var selectionPlanner = sp.GetRequiredService<IWorkflowReadModelSelectionPlanner>();
            var storeFactory = sp.GetRequiredService<IProjectionReadModelStoreFactory>();
            var selectionPlan = selectionPlanner.Build(options);

            return storeFactory.Create<WorkflowExecutionReport, string>(
                sp,
                selectionPlan.SelectionOptions,
                selectionPlan.Requirements);
        }));
    }

    private static void RegisterWorkflowRelationStoreSelector(IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<IProjectionRelationStore>(sp =>
        {
            var options = sp.GetRequiredService<WorkflowExecutionProjectionOptions>();
            var selectionPlanner = sp.GetRequiredService<IWorkflowReadModelSelectionPlanner>();
            var relationStoreFactory = sp.GetRequiredService<IProjectionRelationStoreFactory>();
            var selectionPlan = selectionPlanner.Build(options);

            return relationStoreFactory.Create(
                sp,
                selectionPlan.SelectionOptions,
                selectionPlan.Requirements);
        }));
    }

    private sealed class PassthroughEventDeduplicator : IEventDeduplicator
    {
        public Task<bool> TryRecordAsync(string eventId)
        {
            _ = eventId;
            return Task.FromResult(true);
        }
    }
}
