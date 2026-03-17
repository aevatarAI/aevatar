using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Core.Streaming;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Abstractions.Evolution;
using Aevatar.Scripting.Core.Materialization;
using Aevatar.Scripting.Core.Ports;
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
        services.TryAddSingleton<ScriptExecutionReadModelPort>();
        services.TryAddSingleton<IScriptExecutionReadModelActivationPort>(sp =>
            sp.GetRequiredService<ScriptExecutionReadModelPort>());
        services.TryAddSingleton<ScriptAuthorityProjectionPort>();
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
        services.TryAddSingleton<ScriptEvolutionReadModelPort>();
        services.TryAddSingleton<IScriptEvolutionReadModelActivationPort>(sp =>
            sp.GetRequiredService<ScriptEvolutionReadModelPort>());
        services.TryAddSingleton<IScriptEvolutionDecisionReadPort, ProjectionScriptEvolutionDecisionReadPort>();
        services.TryAddSingleton<ScriptReadModelQueryReader>();
        services.TryAddSingleton<IScriptReadModelQueryPort>(sp =>
            sp.GetRequiredService<ScriptReadModelQueryReader>());
        services.TryAddSingleton<IScriptDefinitionSnapshotPort, ProjectionScriptDefinitionSnapshotPort>();
        services.TryAddSingleton<IScriptCatalogQueryPort, ProjectionScriptCatalogQueryPort>();
        services.TryAddSingleton<IScriptAuthorityReadModelActivationPort>(sp =>
            sp.GetRequiredService<ScriptAuthorityProjectionPort>());
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

        return services;
    }
}
