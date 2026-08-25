using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Workflow.Projection.Audit;
using Aevatar.Workflow.Projection.Configuration;
using Aevatar.Workflow.Projection.Metadata;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.Projectors;
using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.Workflow.Projection.Workflows;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Schedules;
using Aevatar.Workflow.Application.Abstractions.Workflows;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Runtime.DependencyInjection;
using Aevatar.CQRS.Projection.Runtime.Runtime;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Core.Streaming;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.Foundation.Projection.Runtime;
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
        services.AddAevatarAgentKindRegistry(builder =>
            builder.ScanAssemblies(typeof(WorkflowExecutionMaterializationScopeGAgent).Assembly));
        services.AddRuntimeFleetCapabilityProjection();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IRuntimeFleetCapabilityAdvertisement,
            WorkflowProjectionIncrementalGraphCapabilityAdvertisement>());
        services.TryAddSingleton<IProjectionRuntimeOptions>(sp =>
            sp.GetRequiredService<WorkflowExecutionProjectionOptions>());
        services.AddProjectionReadModelRuntime();
        // Heal/guard the workflow binding write path so a definition actor's binding slot can never
        // stay Run-kind (a relayed run-bind clobber frozen at a high run version). Scoped to
        // WorkflowActorBindingDocument only; the generic ProjectionWriteResultEvaluator is untouched.
        services.AddSingleton<ObservedProjectionWriteDispatcher<WorkflowActorBindingDocument>>();
        services.AddSingleton<IProjectionWriteDispatcher<WorkflowActorBindingDocument>>(sp =>
            new WorkflowActorBindingHealingWriteDispatcher(
                sp.GetRequiredService<ObservedProjectionWriteDispatcher<WorkflowActorBindingDocument>>(),
                sp.GetRequiredService<IProjectionDocumentReader<WorkflowActorBindingDocument, string>>()));
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<WorkflowExecutionCurrentStateDocument>, WorkflowExecutionCurrentStateDocumentMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<WorkflowRunInsightReportDocument>, WorkflowRunInsightReportDocumentMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<WorkflowActorBindingDocument>, WorkflowActorBindingDocumentMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<WorkflowCatalogCurrentStateDocument>, WorkflowCatalogCurrentStateDocumentMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<WorkflowExternalApprovalContinuationDocument>, WorkflowExternalApprovalContinuationDocumentMetadataProvider>();
        services.TryAddSingleton<IProjectionClock, SystemProjectionClock>();
        services.TryAddSingleton<WorkflowExecutionReadModelMapper>();
        services.TryAddSingleton<WorkflowRunIncrementalGraphMaterializer>();
        services.TryAddSingleton<WorkflowProjectionGraphCutoverOrchestrator>();
        services.TryAddSingleton<WorkflowCatalogReadModelMapper>();
        services.TryAddSingleton<WorkflowCatalogReadModelQueryPort>();
        services.TryAddSingleton<IProjectionGraphMaterializer<WorkflowRunInsightReportDocument>, WorkflowRunInsightReportGraphMaterializer>();
        services.AddProjectionMaterializationRuntimeCore<
            WorkflowExecutionMaterializationContext,
            WorkflowExecutionMaterializationRuntimeLease,
            WorkflowExecutionMaterializationScopeGAgent>(
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
        AddWorkflowDefinitionBindObservation(services);
        services.TryAddSingleton<WorkflowExecutionCurrentStateQueryPort>();
        AddWorkflowTerminalStateReconciliation(services);
        services.TryAddSingleton<WorkflowExecutionArtifactQueryPort>();
        services.TryAddSingleton<WorkflowRunForkSeedReadModelMapper>();
        services.TryAddSingleton<WorkflowRunForkSeedQueryPort>();
        services.TryAddSingleton<WorkflowExternalApprovalContinuationLookupPort>();
        services.TryAddSingleton<WorkflowExecutionProjectionPort>();
        services.TryAddSingleton<ProjectionActivationPlanDispatcher>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            ICommittedStatePublicationHook,
            CommittedStateProjectionActivationHook>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionActivationPlanProvider,
            WorkflowCommittedStateProjectionActivationPlanProvider>());
        services.TryAddSingleton<ProjectionWorkflowActorBindingReader>();
        services.TryAddSingleton<IWorkflowActorBindingReader>(sp =>
            sp.GetRequiredService<ProjectionWorkflowActorBindingReader>());
        services.TryAddSingleton<IWorkflowRunBindingReader>(sp =>
            sp.GetRequiredService<ProjectionWorkflowActorBindingReader>());
        services.TryAddSingleton<IWorkflowExecutionProjectionPort>(sp =>
            sp.GetRequiredService<WorkflowExecutionProjectionPort>());
        services.TryAddSingleton<
            IWorkflowDefinitionBindObservationScopeLeasePreparationPort,
            WorkflowDefinitionBindObservationScopeLeasePreparationPort>();
        services.TryAddSingleton<
            IWorkflowDefinitionBindObservationProjectionPort,
            WorkflowDefinitionBindObservationProjectionPort>();
        services.TryAddSingleton<IWorkflowExecutionCurrentStateQueryPort>(sp =>
            sp.GetRequiredService<WorkflowExecutionCurrentStateQueryPort>());
        services.TryAddSingleton<IWorkflowRunForkSeedQueryPort>(sp =>
            sp.GetRequiredService<WorkflowRunForkSeedQueryPort>());
        services.TryAddSingleton<IWorkflowExternalApprovalContinuationLookupPort>(sp =>
            sp.GetRequiredService<WorkflowExternalApprovalContinuationLookupPort>());
        services.TryAddSingleton<IWorkflowExecutionArtifactQueryPort>(sp =>
            sp.GetRequiredService<WorkflowExecutionArtifactQueryPort>());
        services.TryAddSingleton<IWorkflowChatRunObservationScopeLeasePreparationPort, WorkflowChatRunObservationScopeLeasePreparationPort>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, WorkflowReadModelStartupValidationHostedService>());
        services.AddProjectionArtifactMaterializer<
            WorkflowBindingProjectionContext,
            WorkflowActorBindingProjector>();
        services.AddCurrentStateProjectionMaterializer<
            WorkflowBindingProjectionContext,
            WorkflowCatalogCurrentStateProjector>();
        services.AddCurrentStateProjectionMaterializer<
            WorkflowExecutionMaterializationContext,
            WorkflowExecutionCurrentStateProjector>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionProjector<WorkflowDefinitionBindObservationProjectionContext>,
            WorkflowDefinitionBindObservationSessionEventProjector>());
        services.AddProjectionArtifactMaterializer<
            WorkflowExecutionMaterializationContext,
            WorkflowExternalApprovalContinuationProjector>();
        services.AddProjectionArtifactMaterializer<
            WorkflowExecutionMaterializationContext,
            WorkflowRunInsightReportArtifactProjector>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IAuditCommittedEventTranslator,
            WorkflowHumanApprovalResolvedAuditTranslator>());
        // Run lifecycle + run-side definition bind: flow through WorkflowRunGAgent's
        // ExecutionMaterialization scope (every WorkflowRunGAgent commit activates it).
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IAuditCommittedEventTranslator,
            WorkflowRunExecutionStartedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IAuditCommittedEventTranslator,
            WorkflowCompletedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IAuditCommittedEventTranslator,
            WorkflowStoppedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IAuditCommittedEventTranslator,
            WorkflowRunStoppedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IAuditCommittedEventTranslator,
            WorkflowRunForkRequestedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IAuditCommittedEventTranslator,
            BindWorkflowRunDefinitionAuditTranslator>());
        // Definition bind: flows through WorkflowGAgent's Binding scope only.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IAuditCommittedEventTranslator,
            BindWorkflowDefinitionAuditTranslator>());
        services.AddAuditCommittedFactMaterializer<WorkflowExecutionMaterializationContext>();
        services.AddAuditCommittedFactMaterializer<WorkflowBindingProjectionContext>();
        return services;
    }

    private static void AddWorkflowTerminalStateReconciliation(IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<WorkflowTerminalStateReconciler>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IHostedService,
            WorkflowTerminalStateReconciliationHostedService>());
    }

    private static void AddWorkflowDefinitionBindObservation(IServiceCollection services)
    {
        services.AddEventSinkProjectionRuntimeCore<
            WorkflowDefinitionBindObservationProjectionContext,
            WorkflowDefinitionBindObservationRuntimeLease,
            EventEnvelope,
            ProjectionSessionScopeGAgent<WorkflowDefinitionBindObservationProjectionContext>>(
            scopeKey => new WorkflowDefinitionBindObservationProjectionContext
            {
                SessionId = scopeKey.SessionId,
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            context => new WorkflowDefinitionBindObservationRuntimeLease(context));
        services.TryAddSingleton<WorkflowBindingSessionEventCodec>();
        services.TryAddSingleton<WorkflowDefinitionBindObservationSessionEventHub>();
    }
}
