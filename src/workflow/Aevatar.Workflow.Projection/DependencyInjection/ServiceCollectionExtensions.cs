using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Workflow.Projection.Configuration;
using Aevatar.Workflow.Projection.Metadata;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.Projectors;
using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Runtime.DependencyInjection;
using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Core.Streaming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

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
        services.AddProjectionReadModelRuntime();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<WorkflowExecutionCurrentStateDocument>, WorkflowExecutionCurrentStateDocumentMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<WorkflowRunTimelineDocument>, WorkflowRunTimelineDocumentMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<WorkflowRunInsightReportDocument>, WorkflowRunInsightReportDocumentMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<WorkflowRunGraphArtifactDocument>, WorkflowRunGraphArtifactDocumentMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<WorkflowActorBindingDocument>, WorkflowActorBindingDocumentMetadataProvider>();
        services.TryAddSingleton<IProjectionClock, SystemProjectionClock>();
        services.TryAddSingleton<WorkflowExecutionReadModelMapper>();
        services.TryAddSingleton<IProjectionGraphMaterializer<WorkflowRunGraphArtifactDocument>, WorkflowRunGraphArtifactMaterializer>();
        services.AddProjectionMaterializationRuntimeCore<
            WorkflowExecutionMaterializationContext,
            WorkflowExecutionMaterializationRuntimeLease>();
        services.AddProjectionMaterializationRuntimeCore<
            WorkflowBindingProjectionContext,
            WorkflowBindingRuntimeLease>();
        services.AddEventSinkProjectionRuntimeCore<
            WorkflowExecutionProjectionContext,
            WorkflowExecutionRuntimeLease,
            WorkflowRunEventEnvelope>();
        services.TryAddSingleton<IProjectionSessionEventCodec<WorkflowRunEventEnvelope>, WorkflowRunEventSessionCodec>();
        services.TryAddSingleton<IProjectionSessionEventHub<WorkflowRunEventEnvelope>, ProjectionSessionEventHub<WorkflowRunEventEnvelope>>();
        services.TryAddSingleton<IProjectionScopeContextFactory<WorkflowExecutionProjectionContext>>(
            _ => new ProjectionScopeContextFactory<WorkflowExecutionProjectionContext>(scopeKey =>
                new WorkflowExecutionProjectionContext
                {
                    SessionId = scopeKey.SessionId,
                    RootActorId = scopeKey.RootActorId,
                    ProjectionKind = scopeKey.ProjectionKind,
                }));
        services.TryAddSingleton<IProjectionScopeContextFactory<WorkflowExecutionMaterializationContext>>(
            _ => new ProjectionScopeContextFactory<WorkflowExecutionMaterializationContext>(scopeKey =>
                new WorkflowExecutionMaterializationContext
                {
                    RootActorId = scopeKey.RootActorId,
                    ProjectionKind = scopeKey.ProjectionKind,
                }));
        services.TryAddSingleton<IProjectionScopeContextFactory<WorkflowBindingProjectionContext>>(
            _ => new ProjectionScopeContextFactory<WorkflowBindingProjectionContext>(scopeKey =>
                new WorkflowBindingProjectionContext
                {
                    RootActorId = scopeKey.RootActorId,
                    ProjectionKind = scopeKey.ProjectionKind,
                }));
        services.TryAddSingleton<IProjectionSessionActivationService<WorkflowExecutionRuntimeLease>>(sp =>
            new ProjectionSessionScopeActivationService<
                WorkflowExecutionRuntimeLease,
                WorkflowExecutionProjectionContext,
                WorkflowExecutionSessionScopeGAgent>(
                sp.GetRequiredService<IActorRuntime>(),
                sp.GetRequiredService<IActorDispatchPort>(),
                request => new WorkflowExecutionProjectionContext
                {
                    SessionId = request.SessionId,
                    RootActorId = request.RootActorId,
                    ProjectionKind = request.ProjectionKind,
                },
                static (_, context) => new WorkflowExecutionRuntimeLease(context),
                sp.GetService<Aevatar.Foundation.Abstractions.TypeSystem.IAgentTypeVerifier>()));
        services.TryAddSingleton<IProjectionSessionReleaseService<WorkflowExecutionRuntimeLease>>(sp =>
            new ProjectionSessionScopeReleaseService<
                WorkflowExecutionRuntimeLease,
                WorkflowExecutionSessionScopeGAgent>(
                sp.GetRequiredService<IActorRuntime>(),
                sp.GetRequiredService<IActorDispatchPort>(),
                lease => new ProjectionRuntimeScopeKey(
                    lease.Context.RootActorId,
                    lease.Context.ProjectionKind,
                    ProjectionRuntimeMode.SessionObservation,
                    lease.Context.SessionId),
                sp.GetService<Aevatar.Foundation.Abstractions.TypeSystem.IAgentTypeVerifier>()));
        services.TryAddSingleton<IProjectionMaterializationActivationService<WorkflowExecutionMaterializationRuntimeLease>>(sp =>
            new ProjectionMaterializationScopeActivationService<
                WorkflowExecutionMaterializationRuntimeLease,
                WorkflowExecutionMaterializationContext,
                WorkflowExecutionMaterializationScopeGAgent>(
                sp.GetRequiredService<IActorRuntime>(),
                sp.GetRequiredService<IActorDispatchPort>(),
                request => new WorkflowExecutionMaterializationContext
                {
                    RootActorId = request.RootActorId,
                    ProjectionKind = request.ProjectionKind,
                },
                static (_, context) => new WorkflowExecutionMaterializationRuntimeLease(context),
                sp.GetService<Aevatar.Foundation.Abstractions.TypeSystem.IAgentTypeVerifier>()));
        services.TryAddSingleton<IProjectionMaterializationReleaseService<WorkflowExecutionMaterializationRuntimeLease>>(sp =>
            new ProjectionMaterializationScopeReleaseService<
                WorkflowExecutionMaterializationRuntimeLease,
                WorkflowExecutionMaterializationScopeGAgent>(
                sp.GetRequiredService<IActorRuntime>(),
                sp.GetRequiredService<IActorDispatchPort>(),
                lease => new ProjectionRuntimeScopeKey(
                    lease.Context.RootActorId,
                    lease.Context.ProjectionKind,
                    ProjectionRuntimeMode.DurableMaterialization),
                sp.GetService<Aevatar.Foundation.Abstractions.TypeSystem.IAgentTypeVerifier>()));
        services.TryAddSingleton<IProjectionMaterializationActivationService<WorkflowBindingRuntimeLease>>(sp =>
            new ProjectionMaterializationScopeActivationService<
                WorkflowBindingRuntimeLease,
                WorkflowBindingProjectionContext,
                WorkflowBindingMaterializationScopeGAgent>(
                sp.GetRequiredService<IActorRuntime>(),
                sp.GetRequiredService<IActorDispatchPort>(),
                request => new WorkflowBindingProjectionContext
                {
                    RootActorId = request.RootActorId,
                    ProjectionKind = request.ProjectionKind,
                },
                static (_, context) => new WorkflowBindingRuntimeLease(context),
                sp.GetService<Aevatar.Foundation.Abstractions.TypeSystem.IAgentTypeVerifier>()));
        services.TryAddSingleton<IProjectionMaterializationReleaseService<WorkflowBindingRuntimeLease>>(sp =>
            new ProjectionMaterializationScopeReleaseService<
                WorkflowBindingRuntimeLease,
                WorkflowBindingMaterializationScopeGAgent>(
                sp.GetRequiredService<IActorRuntime>(),
                sp.GetRequiredService<IActorDispatchPort>(),
                lease => new ProjectionRuntimeScopeKey(
                    lease.Context.RootActorId,
                    lease.Context.ProjectionKind,
                    ProjectionRuntimeMode.DurableMaterialization),
                sp.GetService<Aevatar.Foundation.Abstractions.TypeSystem.IAgentTypeVerifier>()));
        services.TryAddSingleton<WorkflowProjectionQueryReader>();
        services.TryAddSingleton<WorkflowExecutionReadModelPort>();
        services.TryAddSingleton<WorkflowExecutionProjectionPort>();
        services.TryAddSingleton<IWorkflowActorBindingReader, ProjectionWorkflowActorBindingReader>();
        services.TryAddSingleton<IWorkflowExecutionReadModelActivationPort, ProjectionWorkflowExecutionReadModelActivationPort>();
        services.TryAddSingleton<IWorkflowExecutionProjectionPort>(sp =>
            sp.GetRequiredService<WorkflowExecutionProjectionPort>());
        services.TryAddSingleton<IWorkflowExecutionProjectionQueryPort>(sp =>
            sp.GetRequiredService<WorkflowProjectionQueryReader>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, WorkflowReadModelStartupValidationHostedService>());
        services.AddProjectionArtifactMaterializer<
            WorkflowBindingProjectionContext,
            WorkflowActorBindingProjector>();
        services.AddCurrentStateProjectionMaterializer<
            WorkflowExecutionMaterializationContext,
            WorkflowExecutionCurrentStateProjector>();
        services.AddProjectionArtifactMaterializer<
            WorkflowExecutionMaterializationContext,
            WorkflowRunInsightReportArtifactProjector>();
        services.AddProjectionArtifactMaterializer<
            WorkflowExecutionMaterializationContext,
            WorkflowRunTimelineArtifactProjector>();
        services.AddProjectionArtifactMaterializer<
            WorkflowExecutionMaterializationContext,
            WorkflowRunGraphArtifactProjector>();
        return services;
    }
}
