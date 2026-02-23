using Aevatar.Workflow.Projection.Configuration;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Foundation.Abstractions.Deduplication;
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
        services.TryAddSingleton<IEventDeduplicator, PassthroughEventDeduplicator>();
        RegisterWorkflowReadModelStoreSelector(services);
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

    private static void RegisterWorkflowReadModelStoreSelector(IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<IProjectionReadModelStore<WorkflowExecutionReport, string>>(sp =>
        {
            var options = sp.GetRequiredService<WorkflowExecutionProjectionOptions>();
            var requirements = ResolveReadModelRequirements(options, typeof(WorkflowExecutionReport));
            var selectionOptions = new ProjectionReadModelStoreSelectionOptions
            {
                RequestedProviderName = NormalizeProviderName(options.ReadModelProvider),
                FailOnUnsupportedCapabilities = options.FailOnUnsupportedCapabilities,
            };

            var registration = ProjectionReadModelStoreSelector.Select(
                sp.GetServices<IProjectionReadModelStoreRegistration<WorkflowExecutionReport, string>>(),
                selectionOptions,
                requirements);

            return registration.Create(sp);
        }));
    }

    private static ProjectionReadModelRequirements ResolveReadModelRequirements(
        WorkflowExecutionProjectionOptions options,
        Type readModelType)
    {
        if (!TryResolveIndexKindBinding(options.ReadModelBindings, readModelType, out var requiredKind))
            return new ProjectionReadModelRequirements();

        return new ProjectionReadModelRequirements(
            requiresIndexing: true,
            requiredIndexKinds: [requiredKind]);
    }

    private static bool TryResolveIndexKindBinding(
        IReadOnlyDictionary<string, string> readModelBindings,
        Type readModelType,
        out ProjectionReadModelIndexKind requiredKind)
    {
        requiredKind = ProjectionReadModelIndexKind.None;
        if (readModelBindings.Count == 0)
            return false;

        if (!TryGetBinding(readModelBindings, readModelType.Name, out var configuredIndexKind) &&
            !TryGetBinding(readModelBindings, readModelType.FullName ?? "", out configuredIndexKind))
            return false;

        if (!Enum.TryParse<ProjectionReadModelIndexKind>(configuredIndexKind, true, out requiredKind) ||
            requiredKind == ProjectionReadModelIndexKind.None)
        {
            throw new InvalidOperationException(
                $"Invalid ReadModelBindings value '{configuredIndexKind}' for '{readModelType.FullName}'. " +
                $"Allowed values: {ProjectionReadModelIndexKind.Document}, {ProjectionReadModelIndexKind.Graph}.");
        }

        return true;
    }

    private static bool TryGetBinding(
        IReadOnlyDictionary<string, string> readModelBindings,
        string key,
        out string value)
    {
        if (key.Length > 0 && readModelBindings.TryGetValue(key, out value!))
            return true;

        value = "";
        return false;
    }

    private static string NormalizeProviderName(string providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName))
            return ProjectionReadModelProviderNames.InMemory;

        return providerName.Trim();
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
