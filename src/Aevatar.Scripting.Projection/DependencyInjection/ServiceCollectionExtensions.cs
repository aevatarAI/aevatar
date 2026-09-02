using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Core.Streaming;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Abstractions.Evolution;
using Aevatar.Scripting.Core.Materialization;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Projection.Audit;
using Aevatar.Scripting.Projection.Configuration;
using Aevatar.Scripting.Projection.Materialization;
using Aevatar.Scripting.Projection.Metadata;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.Projectors;
using Aevatar.Scripting.Projection.Queries;
using Aevatar.Scripting.Projection.ReadPorts;
using Aevatar.Scripting.Projection.ReadModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Scripting.Projection.DependencyInjection;

public static class ServiceCollectionExtensions
{
    // Refactor (iter76/cluster-076-scripting-domain-fact-derived-readmodel-payloads):
    //   Old pattern: ScriptDomainFactCommitted persisted derived readmodel/native_document/native_graph payloads inside the domain event
    //   New principle: domain event keeps only committed facts; projection materializer derives readmodel/native_document/(optional)native_graph from fact + state_root
    public static IServiceCollection AddScriptingProjectionComponents(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(new ScriptExecutionProjectionOptions());
        services.TryAddSingleton(new ScriptEvolutionProjectionOptions());
        services.TryAddSingleton<IProjectionClock, SystemProjectionClock>();
        services.TryAddSingleton<IProjectionSessionEventCodec<EventEnvelope>, ScriptExecutionSessionEventCodec>();
        services.TryAddSingleton<IProjectionSessionEventHub<EventEnvelope>, ProjectionSessionEventHub<EventEnvelope>>();
        services.AddProjectionMaterializationRuntimeCore<
            ScriptExecutionMaterializationContext,
            ScriptExecutionMaterializationRuntimeLease,
            ProjectionMaterializationScopeGAgent<ScriptExecutionMaterializationContext>>(
            scopeKey => new ScriptExecutionMaterializationContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            context => new ScriptExecutionMaterializationRuntimeLease(context));
        services.AddEventSinkProjectionRuntimeCore<
            ScriptExecutionProjectionContext,
            ScriptExecutionRuntimeLease,
            EventEnvelope,
            ProjectionSessionScopeGAgent<ScriptExecutionProjectionContext>>(
            scopeKey => new ScriptExecutionProjectionContext
            {
                SessionId = scopeKey.SessionId,
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            context => new ScriptExecutionRuntimeLease(context));
        services.AddProjectionMaterializationRuntimeCore<
            ScriptAuthorityProjectionContext,
            ScriptAuthorityRuntimeLease,
            ProjectionMaterializationScopeGAgent<ScriptAuthorityProjectionContext>>(
            scopeKey => new ScriptAuthorityProjectionContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            context => new ScriptAuthorityRuntimeLease(context));
        services.TryAddSingleton<ScriptExecutionProjectionPort>();
        services.TryAddSingleton<IScriptExecutionProjectionPort>(sp =>
            sp.GetRequiredService<ScriptExecutionProjectionPort>());
        services.TryAddSingleton<IProjectionSessionEventCodec<ScriptEvolutionSessionCompletedEvent>, ScriptEvolutionSessionEventCodec>();
        services.TryAddSingleton<IProjectionSessionEventHub<ScriptEvolutionSessionCompletedEvent>, ProjectionSessionEventHub<ScriptEvolutionSessionCompletedEvent>>();
        services.AddProjectionMaterializationRuntimeCore<
            ScriptEvolutionMaterializationContext,
            ScriptEvolutionMaterializationRuntimeLease,
            ProjectionMaterializationScopeGAgent<ScriptEvolutionMaterializationContext>>(
            scopeKey => new ScriptEvolutionMaterializationContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            context => new ScriptEvolutionMaterializationRuntimeLease(context));
        services.AddEventSinkProjectionRuntimeCore<
            ScriptEvolutionSessionProjectionContext,
            ScriptEvolutionRuntimeLease,
            ScriptEvolutionSessionCompletedEvent,
            ProjectionSessionScopeGAgent<ScriptEvolutionSessionProjectionContext>>(
            scopeKey => new ScriptEvolutionSessionProjectionContext
            {
                SessionId = scopeKey.SessionId,
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            context => new ScriptEvolutionRuntimeLease(context));
        services.TryAddSingleton<ScriptEvolutionProjectionPort>();
        services.TryAddSingleton<IScriptEvolutionProjectionPort>(sp =>
            sp.GetRequiredService<ScriptEvolutionProjectionPort>());
        services.TryAddSingleton<IScriptEvolutionDecisionReadPort, ProjectionScriptEvolutionDecisionReadPort>();
        services.TryAddSingleton<ScriptReadModelQueryReader>();
        services.TryAddSingleton<IScriptReadModelQueryPort>(sp =>
            sp.GetRequiredService<ScriptReadModelQueryReader>());
        services.TryAddSingleton<IScriptDefinitionSnapshotPort, ProjectionScriptDefinitionSnapshotPort>();
        services.TryAddSingleton<IScriptCatalogQueryPort, ProjectionScriptCatalogQueryPort>();
        services.TryAddSingleton<ProjectionActivationPlanDispatcher>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            ICommittedStatePublicationHook,
            CommittedStateProjectionActivationHook>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionActivationPlanProvider,
            ScriptingCommittedStateProjectionActivationPlanProvider>());
        services.TryAddSingleton<IScriptProjectionPayloadMaterializer, ScriptProjectionPayloadMaterializer>();
        services.TryAddSingleton<IScriptNativeDocumentMaterializer, ScriptNativeDocumentMaterializer>();
        services.TryAddSingleton<ScriptNativeGraphMaterializer>();
        services.TryAddSingleton<IScriptNativeGraphMaterializer>(sp =>
            sp.GetRequiredService<ScriptNativeGraphMaterializer>());
        services.TryAddSingleton<IProjectionGraphMaterializer<ScriptNativeGraphReadModel>>(sp =>
            sp.GetRequiredService<ScriptNativeGraphMaterializer>());
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ScriptDefinitionSnapshotDocument>, ScriptDefinitionSnapshotDocumentMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ScriptCatalogEntryDocument>, ScriptCatalogEntryDocumentMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ScriptReadModelDocument>, ScriptReadModelDocumentMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ScriptEvolutionReadModel>, ScriptEvolutionReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ScriptNativeDocumentReadModel>, ScriptNativeDocumentReadModelMetadataProvider>();

        services.AddCurrentStateProjectionMaterializer<
            ScriptExecutionMaterializationContext,
            ScriptReadModelProjector>();
        services.AddCurrentStateProjectionMaterializer<
            ScriptExecutionMaterializationContext,
            ScriptNativeDocumentProjector>();
        services.AddCurrentStateProjectionMaterializer<
            ScriptExecutionMaterializationContext,
            ScriptNativeGraphProjector>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionProjector<ScriptExecutionProjectionContext>,
            ScriptExecutionSessionEventProjector>());
        services.AddCurrentStateProjectionMaterializer<
            ScriptAuthorityProjectionContext,
            ScriptDefinitionSnapshotProjector>();
        services.AddCurrentStateProjectionMaterializer<
            ScriptAuthorityProjectionContext,
            ScriptCatalogEntryProjector>();
        services.AddCurrentStateProjectionMaterializer<
            ScriptEvolutionMaterializationContext,
            ScriptEvolutionReadModelProjector>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionProjector<ScriptEvolutionSessionProjectionContext>,
            ScriptEvolutionSessionCompletedEventProjector>());

        // ─── Committed-fact audit (platform governance trail) ───
        // Single-write governance facts translated into the audit trail. The
        // authority events (definition upsert + catalog promote/rollback) are
        // observed by the authority materialization scope their own commit
        // activates; the terminal evolution-session decision is observed by the
        // evolution materialization scope. Double-written evolution index-mirror
        // events are intentionally NOT registered (see audit translators file).
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IAuditCommittedEventTranslator, ScriptDefinitionUpsertedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IAuditCommittedEventTranslator, ScriptCatalogRevisionPromotedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IAuditCommittedEventTranslator, ScriptCatalogRollbackRequestedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IAuditCommittedEventTranslator, ScriptCatalogRolledBackAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IAuditCommittedEventTranslator, ScriptEvolutionSessionCompletedAuditTranslator>());
        // The run-terminal outcome fact is committed by the run actor (ScriptBehaviorGAgent)
        // and observed by the execution materialization scope its own commit activates.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IAuditCommittedEventTranslator, ScriptRunOutcomeRecordedAuditTranslator>());
        services.AddAuditCommittedFactMaterializer<ScriptAuthorityProjectionContext>();
        services.AddAuditCommittedFactMaterializer<ScriptEvolutionMaterializationContext>();
        services.AddAuditCommittedFactMaterializer<ScriptExecutionMaterializationContext>();

        return services;
    }
}
