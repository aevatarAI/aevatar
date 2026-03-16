using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Workflow.Projection.Configuration;
using Aevatar.Workflow.Projection.Metadata;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.Projectors;
using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core;
using Aevatar.Foundation.Abstractions.Deduplication;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Runtime.DependencyInjection;
using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Core.Streaming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Projection.DependencyInjection;

/// <summary>
/// DI registration for chat CQRS projection pipeline.
/// </summary>
public static class ServiceCollectionExtensions
{
    private static readonly Type ProjectionProjectorContract = typeof(IProjectionProjector<,>);
    private static readonly Type WorkflowExecutionProjectorContract = typeof(IProjectionProjector<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>);
    private static readonly Type WorkflowRunInsightProjectorContract = typeof(IProjectionProjector<WorkflowRunInsightProjectionContext, bool>);

    public static IServiceCollection AddWorkflowExecutionProjectionCQRS(
        this IServiceCollection services,
        Action<WorkflowExecutionProjectionOptions>? configure = null)
    {
        var options = new WorkflowExecutionProjectionOptions();
        configure?.Invoke(options);
        services.Replace(ServiceDescriptor.Singleton(options));
        services.TryAddSingleton<IProjectionRuntimeOptions>(sp =>
            sp.GetRequiredService<WorkflowExecutionProjectionOptions>());
        services.TryAddSingleton<IProjectionDispatchCompensationOptions>(sp =>
            sp.GetRequiredService<WorkflowExecutionProjectionOptions>());
        services.TryAddSingleton<IEventDeduplicator, PassthroughEventDeduplicator>();
        services.AddProjectionReadModelRuntime();
        services.TryAddSingleton<IProjectionDispatchCompensationOutbox, ActorProjectionDispatchCompensationOutbox>();
        services.TryAddSingleton<IProjectionStoreDispatchCompensator<WorkflowRunInsightReportDocument>, DurableProjectionDispatchCompensator<WorkflowRunInsightReportDocument>>();
        services.Replace(ServiceDescriptor.Singleton<IWorkflowRunDetachedCleanupScheduler, ActorWorkflowRunDetachedCleanupOutbox>());
        services.TryAddSingleton<IWorkflowRunDetachedCleanupOutbox>(sp =>
            (ActorWorkflowRunDetachedCleanupOutbox)sp.GetRequiredService<IWorkflowRunDetachedCleanupScheduler>());
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<WorkflowExecutionCurrentStateDocument>, WorkflowExecutionCurrentStateDocumentMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<WorkflowRunTimelineDocument>, WorkflowRunTimelineDocumentMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<WorkflowRunInsightReportDocument>, WorkflowRunInsightReportDocumentMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<WorkflowActorBindingDocument>, WorkflowActorBindingDocumentMetadataProvider>();
        services.TryAddSingleton<IProjectionClock, SystemProjectionClock>();
        services.TryAddSingleton<IWorkflowExecutionProjectionContextFactory, DefaultWorkflowExecutionProjectionContextFactory>();
        services.TryAddSingleton<WorkflowExecutionReadModelMapper>();
        services.TryAddSingleton<IWorkflowRunInsightActorPort, ActorWorkflowRunInsightPort>();
        services.TryAddSingleton<IProjectionGraphMaterializer<WorkflowRunGraphMirrorReadModel>, WorkflowRunGraphMirrorMaterializer>();
        RegisterFromAssembly(services, typeof(ServiceCollectionExtensions).Assembly);
        services.TryAddSingleton<IProjectionSessionEventCodec<EventEnvelope>, WorkflowBindingSessionEventCodec>();
        services.TryAddSingleton<IProjectionSessionEventHub<EventEnvelope>, ProjectionSessionEventHub<EventEnvelope>>();
        services.AddEventSinkProjectionRuntimeCore<
            WorkflowBindingProjectionContext,
            IReadOnlyList<string>,
            WorkflowBindingRuntimeLease,
            EventEnvelope>();
        services.AddEventSinkProjectionRuntimeCore<
            WorkflowExecutionProjectionContext,
            IReadOnlyList<WorkflowExecutionTopologyEdge>,
            WorkflowExecutionRuntimeLease,
            WorkflowRunEventEnvelope,
            WorkflowProjectionSinkFailurePolicy>();
        services.TryAddSingleton<IProjectionDispatchFailureReporter<WorkflowExecutionProjectionContext>, WorkflowProjectionDispatchFailureReporter>();
        services.TryAddSingleton(typeof(IActorStreamSubscriptionHub<>), typeof(ActorStreamSubscriptionHub<>));
        services.TryAddSingleton(sp => new ProjectionOwnershipCoordinatorOptions
        {
            LeaseTtlMs = sp.GetRequiredService<WorkflowExecutionProjectionOptions>().ProjectionOwnershipLeaseTtlMs,
        });
        services.TryAddSingleton<IProjectionOwnershipCoordinator, ActorProjectionOwnershipCoordinator>();
        services.TryAddSingleton<IProjectionSessionEventCodec<WorkflowRunEventEnvelope>, WorkflowRunEventSessionCodec>();
        services.TryAddSingleton<IProjectionSessionEventHub<WorkflowRunEventEnvelope>, ProjectionSessionEventHub<WorkflowRunEventEnvelope>>();
        services.TryAddSingleton<IProjectionSessionEventCodec<WorkflowProjectionControlEvent>, WorkflowProjectionControlEventSessionCodec>();
        services.TryAddSingleton<IProjectionSessionEventHub<WorkflowProjectionControlEvent>, ProjectionSessionEventHub<WorkflowProjectionControlEvent>>();
        services.TryAddSingleton<IProjectionPortActivationService<WorkflowExecutionRuntimeLease>>(sp =>
        {
            var lifecycle = sp.GetRequiredService<IProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>>();
            var clock = sp.GetRequiredService<IProjectionClock>();
            var contextFactory = sp.GetRequiredService<IWorkflowExecutionProjectionContextFactory>();
            var ownershipCoordinator = sp.GetRequiredService<IProjectionOwnershipCoordinator>();
            var ownershipOptions = sp.GetRequiredService<ProjectionOwnershipCoordinatorOptions>();
            var projectionControlHub = sp.GetService<IProjectionSessionEventHub<WorkflowProjectionControlEvent>>();
            var runtimeLeaseLogger = sp.GetService<ILogger<WorkflowExecutionRuntimeLease>>();

            return new ContextProjectionActivationService<WorkflowExecutionRuntimeLease, WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>(
                lifecycle,
                (rootEntityId, workflowName, input, commandId, _) => contextFactory.Create(
                    rootEntityId,
                    commandId,
                    rootEntityId,
                    workflowName,
                    input,
                    clock.UtcNow),
                context => new WorkflowExecutionRuntimeLease(
                    context,
                    ownershipCoordinator,
                    ownershipOptions,
                    lifecycle,
                    projectionControlHub,
                    runtimeLeaseLogger),
                acquireBeforeStart: (rootEntityId, _, _, commandId, ct) =>
                    ownershipCoordinator.AcquireAsync(rootEntityId, commandId, ct),
                onRuntimeLeaseCreated: async (_, _, _, runtimeLease, ct) =>
                {
                    try
                    {
                        await runtimeLease.WaitForProjectionReleaseListenerReadyAsync(ct);
                    }
                    catch
                    {
                        await TryStopRuntimeLeaseAsync(runtimeLease);
                        throw;
                    }
                },
                cleanupOnStartFailure: (rootEntityId, commandId) =>
                    TryReleaseProjectionOwnershipAsync(ownershipCoordinator, rootEntityId, commandId));
        });
        services.TryAddSingleton<IProjectionPortActivationService<WorkflowRunInsightRuntimeLease>>(sp =>
            new ContextProjectionActivationService<WorkflowRunInsightRuntimeLease, WorkflowRunInsightProjectionContext, bool>(
                sp.GetRequiredService<IProjectionLifecycleService<WorkflowRunInsightProjectionContext, bool>>(),
                (rootEntityId, _, _, _, _) => new WorkflowRunInsightProjectionContext
                {
                    ProjectionId = $"{rootEntityId}:insight",
                    RootActorId = WorkflowRunInsightGAgent.BuildActorId(rootEntityId),
                    RunActorId = rootEntityId,
                },
                context => new WorkflowRunInsightRuntimeLease(context)));
        services.TryAddSingleton<IProjectionPortReleaseService<WorkflowExecutionRuntimeLease>, ContextProjectionReleaseService<WorkflowExecutionRuntimeLease, WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>>();
        services.TryAddSingleton<IProjectionPortActivationService<WorkflowBindingRuntimeLease>>(sp =>
            new ContextProjectionActivationService<WorkflowBindingRuntimeLease, WorkflowBindingProjectionContext, IReadOnlyList<string>>(
                sp.GetRequiredService<IProjectionLifecycleService<WorkflowBindingProjectionContext, IReadOnlyList<string>>>(),
                (rootEntityId, _, _, _, _) => new WorkflowBindingProjectionContext
                {
                    ProjectionId = $"{rootEntityId}:binding",
                    RootActorId = rootEntityId,
                },
                context => new WorkflowBindingRuntimeLease(context)));
        services.TryAddSingleton<IProjectionPortReleaseService<WorkflowBindingRuntimeLease>, ContextProjectionReleaseService<WorkflowBindingRuntimeLease, WorkflowBindingProjectionContext, IReadOnlyList<string>>>();
        services.TryAddSingleton<WorkflowProjectionQueryReader>();
        services.TryAddSingleton<WorkflowExecutionProjectionPort>();
        services.TryAddSingleton<IWorkflowActorBindingReader, ProjectionWorkflowActorBindingReader>();
        services.TryAddSingleton<IWorkflowExecutionProjectionPort>(sp =>
            sp.GetRequiredService<WorkflowExecutionProjectionPort>());
        services.TryAddSingleton<IWorkflowExecutionProjectionQueryPort>(sp =>
            sp.GetRequiredService<WorkflowProjectionQueryReader>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ProjectionDispatchCompensationReplayHostedService>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, WorkflowRunDetachedCleanupReplayHostedService>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, WorkflowReadModelStartupValidationHostedService>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionProjector<WorkflowBindingProjectionContext, IReadOnlyList<string>>,
            WorkflowActorBindingProjector>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionProjector<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>,
            WorkflowExecutionCurrentStateProjector>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionProjector<WorkflowRunInsightProjectionContext, bool>,
            WorkflowRunInsightReportDocumentProjector>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionProjector<WorkflowRunInsightProjectionContext, bool>,
            WorkflowRunTimelineReadModelProjector>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionProjector<WorkflowRunInsightProjectionContext, bool>,
            WorkflowRunGraphMirrorProjector>());
        return services;
    }

    /// <summary>
    /// Registers a custom projector from another assembly/module.
    /// </summary>
    public static IServiceCollection AddWorkflowRunInsightBridgeProjector<TProjector>(this IServiceCollection services)
        where TProjector : class, IProjectionProjector<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton(
            typeof(IProjectionProjector<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>),
            typeof(TProjector)));
        return services;
    }

    private static void RegisterFromAssembly(IServiceCollection services, Assembly assembly)
    {
        ProjectionAssemblyRegistration.RegisterProjectorExtensionsFromAssembly(
            services,
            assembly,
            WorkflowExecutionProjectorContract,
            ProjectionProjectorContract);
        ProjectionAssemblyRegistration.RegisterProjectorExtensionsFromAssembly(
            services,
            assembly,
            WorkflowRunInsightProjectorContract,
            ProjectionProjectorContract);
    }

    private static async Task TryReleaseProjectionOwnershipAsync(
        IProjectionOwnershipCoordinator ownershipCoordinator,
        string rootActorId,
        string commandId)
    {
        try
        {
            await ownershipCoordinator.ReleaseAsync(rootActorId, commandId, CancellationToken.None);
        }
        catch
        {
            // Best effort cleanup: ownership may already be released or unavailable.
        }
    }

    private static async Task TryStopRuntimeLeaseAsync(WorkflowExecutionRuntimeLease runtimeLease)
    {
        try
        {
            await runtimeLease.StopProjectionReleaseListenerAsync();
        }
        catch
        {
            // Preserve the activation failure.
        }

        try
        {
            await runtimeLease.StopOwnershipHeartbeatAsync();
        }
        catch
        {
            // Preserve the activation failure.
        }
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
