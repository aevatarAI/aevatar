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
        services.TryAddSingleton(typeof(IActorStreamSubscriptionHub<>), typeof(ActorStreamSubscriptionHub<>));
        services.TryAddSingleton<IProjectionSessionEventCodec<EventEnvelope>, ScriptExecutionSessionEventCodec>();
        services.TryAddSingleton<IProjectionSessionEventHub<EventEnvelope>, ProjectionSessionEventHub<EventEnvelope>>();
        services.AddProjectionMaterializationRuntimeCore<
            ScriptExecutionMaterializationContext,
            ScriptExecutionMaterializationRuntimeLease>();
        services.AddEventSinkProjectionRuntimeCore<
            ScriptExecutionProjectionContext,
            ScriptExecutionRuntimeLease,
            EventEnvelope>();
        services.AddProjectionMaterializationRuntimeCore<
            ScriptAuthorityProjectionContext,
            ScriptAuthorityRuntimeLease>();
        services.TryAddSingleton<IProjectionSessionActivationService<ScriptExecutionRuntimeLease>>(sp =>
            new ContextProjectionActivationService<ScriptExecutionRuntimeLease, ScriptExecutionProjectionContext>(
                sp.GetRequiredService<IProjectionLifecycleService<ScriptExecutionProjectionContext, ScriptExecutionRuntimeLease>>(),
                (request, _) => new ScriptExecutionProjectionContext
                {
                    SessionId = request.SessionId,
                    RootActorId = request.RootActorId,
                    ProjectionKind = request.ProjectionKind,
                },
                context => new ScriptExecutionRuntimeLease(context)));
        services.TryAddSingleton<IProjectionSessionReleaseService<ScriptExecutionRuntimeLease>, ContextProjectionReleaseService<ScriptExecutionRuntimeLease, ScriptExecutionProjectionContext>>();
        services.TryAddSingleton<ScriptExecutionProjectionPort>();
        services.TryAddSingleton<IScriptExecutionProjectionPort>(sp =>
            sp.GetRequiredService<ScriptExecutionProjectionPort>());
        services.TryAddSingleton<IProjectionMaterializationActivationService<ScriptExecutionMaterializationRuntimeLease>>(sp =>
            new ContextProjectionMaterializationActivationService<ScriptExecutionMaterializationRuntimeLease, ScriptExecutionMaterializationContext>(
                sp.GetRequiredService<IProjectionMaterializationLifecycleService<ScriptExecutionMaterializationContext, ScriptExecutionMaterializationRuntimeLease>>(),
                (request, _) => new ScriptExecutionMaterializationContext
                {
                    RootActorId = request.RootActorId,
                    ProjectionKind = request.ProjectionKind,
                },
                context => new ScriptExecutionMaterializationRuntimeLease(context)));
        services.TryAddSingleton<IProjectionMaterializationReleaseService<ScriptExecutionMaterializationRuntimeLease>, ContextProjectionMaterializationReleaseService<ScriptExecutionMaterializationRuntimeLease, ScriptExecutionMaterializationContext>>();
        services.TryAddSingleton<ScriptExecutionReadModelPort>();
        services.TryAddSingleton<IScriptExecutionReadModelActivationPort, ProjectionScriptExecutionReadModelActivationPort>();
        services.TryAddSingleton<IProjectionMaterializationActivationService<ScriptAuthorityRuntimeLease>>(sp =>
            new ContextProjectionMaterializationActivationService<ScriptAuthorityRuntimeLease, ScriptAuthorityProjectionContext>(
                sp.GetRequiredService<IProjectionMaterializationLifecycleService<ScriptAuthorityProjectionContext, ScriptAuthorityRuntimeLease>>(),
                (request, _) => new ScriptAuthorityProjectionContext
                {
                    RootActorId = request.RootActorId,
                    ProjectionKind = request.ProjectionKind,
                },
                context => new ScriptAuthorityRuntimeLease(context)));
        services.TryAddSingleton<IProjectionMaterializationReleaseService<ScriptAuthorityRuntimeLease>, ContextProjectionMaterializationReleaseService<ScriptAuthorityRuntimeLease, ScriptAuthorityProjectionContext>>();
        services.TryAddSingleton<ScriptAuthorityProjectionPort>();
        services.TryAddSingleton<IProjectionSessionEventCodec<ScriptEvolutionSessionCompletedEvent>, ScriptEvolutionSessionEventCodec>();
        services.TryAddSingleton<IProjectionSessionEventHub<ScriptEvolutionSessionCompletedEvent>, ProjectionSessionEventHub<ScriptEvolutionSessionCompletedEvent>>();
        services.AddProjectionMaterializationRuntimeCore<
            ScriptEvolutionMaterializationContext,
            ScriptEvolutionMaterializationRuntimeLease>();
        services.AddEventSinkProjectionRuntimeCore<
            ScriptEvolutionSessionProjectionContext,
            ScriptEvolutionRuntimeLease,
            ScriptEvolutionSessionCompletedEvent>();
        services.TryAddSingleton<IProjectionSessionActivationService<ScriptEvolutionRuntimeLease>>(sp =>
            new ContextProjectionActivationService<ScriptEvolutionRuntimeLease, ScriptEvolutionSessionProjectionContext>(
                sp.GetRequiredService<IProjectionLifecycleService<ScriptEvolutionSessionProjectionContext, ScriptEvolutionRuntimeLease>>(),
                (request, _) => new ScriptEvolutionSessionProjectionContext
                {
                    SessionId = request.SessionId,
                    RootActorId = request.RootActorId,
                    ProjectionKind = request.ProjectionKind,
                },
                context => new ScriptEvolutionRuntimeLease(context)));
        services.TryAddSingleton<IProjectionSessionReleaseService<ScriptEvolutionRuntimeLease>, ContextProjectionReleaseService<ScriptEvolutionRuntimeLease, ScriptEvolutionSessionProjectionContext>>();
        services.TryAddSingleton<ScriptEvolutionProjectionPort>();
        services.TryAddSingleton<IScriptEvolutionProjectionPort>(sp =>
            sp.GetRequiredService<ScriptEvolutionProjectionPort>());
        services.TryAddSingleton<IProjectionMaterializationActivationService<ScriptEvolutionMaterializationRuntimeLease>>(sp =>
            new ContextProjectionMaterializationActivationService<ScriptEvolutionMaterializationRuntimeLease, ScriptEvolutionMaterializationContext>(
                sp.GetRequiredService<IProjectionMaterializationLifecycleService<ScriptEvolutionMaterializationContext, ScriptEvolutionMaterializationRuntimeLease>>(),
                (request, _) => new ScriptEvolutionMaterializationContext
                {
                    RootActorId = request.RootActorId,
                    ProjectionKind = request.ProjectionKind,
                },
                context => new ScriptEvolutionMaterializationRuntimeLease(context)));
        services.TryAddSingleton<IProjectionMaterializationReleaseService<ScriptEvolutionMaterializationRuntimeLease>, ContextProjectionMaterializationReleaseService<ScriptEvolutionMaterializationRuntimeLease, ScriptEvolutionMaterializationContext>>();
        services.TryAddSingleton<ScriptEvolutionReadModelPort>();
        services.TryAddSingleton<IScriptEvolutionReadModelActivationPort, ProjectionScriptEvolutionReadModelActivationPort>();
        services.TryAddSingleton<IScriptEvolutionDecisionReadPort, ProjectionScriptEvolutionDecisionReadPort>();
        services.TryAddSingleton<ScriptReadModelQueryReader>();
        services.TryAddSingleton<IScriptReadModelQueryPort>(sp =>
            sp.GetRequiredService<ScriptReadModelQueryReader>());
        services.TryAddSingleton<IScriptDefinitionSnapshotPort, ProjectionScriptDefinitionSnapshotPort>();
        services.TryAddSingleton<IScriptCatalogQueryPort, ProjectionScriptCatalogQueryPort>();
        services.TryAddSingleton<IScriptAuthorityReadModelActivationPort, ProjectionScriptAuthorityReadModelActivationPort>();
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

        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionMaterializer<ScriptExecutionMaterializationContext>,
            ScriptReadModelProjector>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionMaterializer<ScriptExecutionMaterializationContext>,
            ScriptNativeDocumentProjector>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionMaterializer<ScriptExecutionMaterializationContext>,
            ScriptNativeGraphProjector>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionProjector<ScriptExecutionProjectionContext>,
            ScriptExecutionSessionEventProjector>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionMaterializer<ScriptAuthorityProjectionContext>,
            ScriptDefinitionSnapshotProjector>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionMaterializer<ScriptAuthorityProjectionContext>,
            ScriptCatalogEntryProjector>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionMaterializer<ScriptEvolutionMaterializationContext>,
            ScriptEvolutionReadModelProjector>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionProjector<ScriptEvolutionSessionProjectionContext>,
            ScriptEvolutionSessionCompletedEventProjector>());

        return services;
    }
}
