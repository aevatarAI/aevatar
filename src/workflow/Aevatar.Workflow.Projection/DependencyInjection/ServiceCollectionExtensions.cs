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
            WorkflowExecutionMaterializationRuntimeLease,
            ProjectionMaterializationScopeGAgent<WorkflowExecutionMaterializationContext>>(
            scopeKey => new WorkflowExecutionMaterializationContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            context => new WorkflowExecutionMaterializationRuntimeLease(context));
        services.AddProjectionMaterializationRuntimeCore<
            WorkflowBindingProjectionContext,
            WorkflowBindingRuntimeLease,
            ProjectionMaterializationScopeGAgent<WorkflowBindingProjectionContext>>(
            scopeKey => new WorkflowBindingProjectionContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            context => new WorkflowBindingRuntimeLease(context));
        services.AddEventSinkProjectionRuntimeCore<
            WorkflowExecutionProjectionContext,
            WorkflowExecutionRuntimeLease,
            WorkflowRunEventEnvelope,
            ProjectionSessionScopeGAgent<WorkflowExecutionProjectionContext>>(
            scopeKey => new WorkflowExecutionProjectionContext
            {
                SessionId = scopeKey.SessionId,
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            context => new WorkflowExecutionRuntimeLease(context));
        services.TryAddSingleton<IProjectionSessionEventCodec<WorkflowRunEventEnvelope>, WorkflowRunEventSessionCodec>();
        services.TryAddSingleton<IProjectionSessionEventHub<WorkflowRunEventEnvelope>, ProjectionSessionEventHub<WorkflowRunEventEnvelope>>();
        services.TryAddSingleton<WorkflowExecutionCurrentStateQueryPort>();
        services.TryAddSingleton<WorkflowExecutionArtifactQueryPort>();
        services.TryAddSingleton<WorkflowExecutionMaterializationPort>();
        services.TryAddSingleton<WorkflowExecutionProjectionPort>();
        services.TryAddSingleton<IWorkflowActorBindingReader, ProjectionWorkflowActorBindingReader>();
        services.TryAddSingleton<IWorkflowExecutionMaterializationActivationPort>(sp =>
            sp.GetRequiredService<WorkflowExecutionMaterializationPort>());
        services.TryAddSingleton<IWorkflowExecutionProjectionPort>(sp =>
            sp.GetRequiredService<WorkflowExecutionProjectionPort>());
        services.TryAddSingleton<IWorkflowExecutionCurrentStateQueryPort>(sp =>
            sp.GetRequiredService<WorkflowExecutionCurrentStateQueryPort>());
        services.TryAddSingleton<IWorkflowExecutionArtifactQueryPort>(sp =>
            sp.GetRequiredService<WorkflowExecutionArtifactQueryPort>());
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
        return services;
    }
}
