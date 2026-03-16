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
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Projection.DependencyInjection;

/// <summary>
/// DI registration for chat CQRS projection pipeline.
/// </summary>
public static class ServiceCollectionExtensions
{
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
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<WorkflowRunGraphMirrorReadModel>, WorkflowRunGraphMirrorReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<WorkflowActorBindingDocument>, WorkflowActorBindingDocumentMetadataProvider>();
        services.TryAddSingleton<IProjectionClock, SystemProjectionClock>();
        services.TryAddSingleton<WorkflowExecutionReadModelMapper>();
        services.TryAddSingleton<IProjectionGraphMaterializer<WorkflowRunGraphMirrorReadModel>, WorkflowRunGraphMirrorMaterializer>();
        services.AddProjectionMaterializationRuntimeCore<
            WorkflowExecutionMaterializationContext,
            WorkflowExecutionMaterializationRuntimeLease>();
        services.AddProjectionMaterializationRuntimeCore<
            WorkflowBindingProjectionContext,
            WorkflowBindingRuntimeLease>();
        services.AddEventSinkProjectionRuntimeCore<
            WorkflowExecutionProjectionContext,
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
        services.TryAddSingleton<IProjectionSessionActivationService<WorkflowExecutionRuntimeLease>>(sp =>
        {
            var lifecycle = sp.GetRequiredService<IProjectionLifecycleService<WorkflowExecutionProjectionContext, WorkflowExecutionRuntimeLease>>();
            var clock = sp.GetRequiredService<IProjectionClock>();
            var ownershipCoordinator = sp.GetRequiredService<IProjectionOwnershipCoordinator>();
            var ownershipOptions = sp.GetRequiredService<ProjectionOwnershipCoordinatorOptions>();
            var projectionControlHub = sp.GetService<IProjectionSessionEventHub<WorkflowProjectionControlEvent>>();
            var runtimeLeaseLogger = sp.GetService<ILogger<WorkflowExecutionRuntimeLease>>();

            return new ContextProjectionActivationService<WorkflowExecutionRuntimeLease, WorkflowExecutionProjectionContext>(
                lifecycle,
                (request, _) => new WorkflowExecutionProjectionContext
                {
                    SessionId = request.SessionId,
                    RootActorId = request.RootActorId,
                    ProjectionKind = request.ProjectionKind,
                },
                context => new WorkflowExecutionRuntimeLease(
                    context,
                    ownershipCoordinator,
                    ownershipOptions,
                    lifecycle,
                    projectionControlHub,
                    runtimeLeaseLogger),
                acquireBeforeStart: (request, ct) =>
                    ownershipCoordinator.AcquireAsync(request.RootActorId, request.SessionId, ct),
                onRuntimeLeaseCreated: async (_, _, runtimeLease, ct) =>
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
                cleanupOnStartFailure: request =>
                    TryReleaseProjectionOwnershipAsync(ownershipCoordinator, request.RootActorId, request.SessionId));
        });
        services.TryAddSingleton<IProjectionSessionReleaseService<WorkflowExecutionRuntimeLease>, ContextProjectionReleaseService<WorkflowExecutionRuntimeLease, WorkflowExecutionProjectionContext>>();
        services.TryAddSingleton<IProjectionMaterializationActivationService<WorkflowExecutionMaterializationRuntimeLease>>(sp =>
            new ContextProjectionMaterializationActivationService<WorkflowExecutionMaterializationRuntimeLease, WorkflowExecutionMaterializationContext>(
                sp.GetRequiredService<IProjectionMaterializationLifecycleService<WorkflowExecutionMaterializationContext, WorkflowExecutionMaterializationRuntimeLease>>(),
                (request, _) => new WorkflowExecutionMaterializationContext
                {
                    RootActorId = request.RootActorId,
                    ProjectionKind = request.ProjectionKind,
                },
                context => new WorkflowExecutionMaterializationRuntimeLease(context)));
        services.TryAddSingleton<IProjectionMaterializationReleaseService<WorkflowExecutionMaterializationRuntimeLease>, ContextProjectionMaterializationReleaseService<WorkflowExecutionMaterializationRuntimeLease, WorkflowExecutionMaterializationContext>>();
        services.TryAddSingleton<IProjectionMaterializationActivationService<WorkflowBindingRuntimeLease>>(sp =>
            new ContextProjectionMaterializationActivationService<WorkflowBindingRuntimeLease, WorkflowBindingProjectionContext>(
                sp.GetRequiredService<IProjectionMaterializationLifecycleService<WorkflowBindingProjectionContext, WorkflowBindingRuntimeLease>>(),
                (request, _) => new WorkflowBindingProjectionContext
                {
                    RootActorId = request.RootActorId,
                    ProjectionKind = request.ProjectionKind,
                },
                context => new WorkflowBindingRuntimeLease(context)));
        services.TryAddSingleton<IProjectionMaterializationReleaseService<WorkflowBindingRuntimeLease>, ContextProjectionMaterializationReleaseService<WorkflowBindingRuntimeLease, WorkflowBindingProjectionContext>>();
        services.TryAddSingleton<WorkflowProjectionQueryReader>();
        services.TryAddSingleton<WorkflowExecutionReadModelPort>();
        services.TryAddSingleton<WorkflowExecutionProjectionPort>();
        services.TryAddSingleton<IWorkflowActorBindingReader, ProjectionWorkflowActorBindingReader>();
        services.TryAddSingleton<IWorkflowExecutionReadModelActivationPort, ProjectionWorkflowExecutionReadModelActivationPort>();
        services.TryAddSingleton<IWorkflowExecutionProjectionPort>(sp =>
            sp.GetRequiredService<WorkflowExecutionProjectionPort>());
        services.TryAddSingleton<IWorkflowExecutionProjectionQueryPort>(sp =>
            sp.GetRequiredService<WorkflowProjectionQueryReader>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ProjectionDispatchCompensationReplayHostedService>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, WorkflowRunDetachedCleanupReplayHostedService>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, WorkflowReadModelStartupValidationHostedService>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionMaterializer<WorkflowBindingProjectionContext>,
            WorkflowActorBindingProjector>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionMaterializer<WorkflowExecutionMaterializationContext>,
            WorkflowExecutionCurrentStateProjector>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionMaterializer<WorkflowExecutionMaterializationContext>,
            WorkflowRunInsightReportDocumentProjector>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionMaterializer<WorkflowExecutionMaterializationContext>,
            WorkflowRunTimelineReadModelProjector>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionMaterializer<WorkflowExecutionMaterializationContext>,
            WorkflowRunGraphMirrorProjector>());
        return services;
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
