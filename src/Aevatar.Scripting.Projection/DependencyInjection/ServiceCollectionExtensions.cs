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
        services.AddEventSinkProjectionRuntimeCore<
            ScriptExecutionProjectionContext,
            IReadOnlyList<string>,
            ScriptExecutionRuntimeLease,
            EventEnvelope>();
        services.AddEventSinkProjectionRuntimeCore<
            ScriptAuthorityProjectionContext,
            IReadOnlyList<string>,
            ScriptAuthorityRuntimeLease,
            EventEnvelope>();
        services.TryAddSingleton<IProjectionPortActivationService<ScriptExecutionRuntimeLease>>(sp =>
            new ContextProjectionActivationService<ScriptExecutionRuntimeLease, ScriptExecutionProjectionContext, IReadOnlyList<string>>(
                sp.GetRequiredService<IProjectionLifecycleService<ScriptExecutionProjectionContext, IReadOnlyList<string>>>(),
                (rootEntityId, _, _, _, _) => new ScriptExecutionProjectionContext
                {
                    ProjectionId = $"{rootEntityId}:read-model",
                    RootActorId = rootEntityId,
                },
                context => new ScriptExecutionRuntimeLease(context)));
        services.TryAddSingleton<IProjectionPortReleaseService<ScriptExecutionRuntimeLease>, ContextProjectionReleaseService<ScriptExecutionRuntimeLease, ScriptExecutionProjectionContext, IReadOnlyList<string>>>();
        services.TryAddSingleton<ScriptExecutionProjectionPortService>();
        services.TryAddSingleton<IScriptExecutionProjectionPort>(sp =>
            sp.GetRequiredService<ScriptExecutionProjectionPortService>());
        services.TryAddSingleton<IProjectionPortActivationService<ScriptAuthorityRuntimeLease>>(sp =>
            new ContextProjectionActivationService<ScriptAuthorityRuntimeLease, ScriptAuthorityProjectionContext, IReadOnlyList<string>>(
                sp.GetRequiredService<IProjectionLifecycleService<ScriptAuthorityProjectionContext, IReadOnlyList<string>>>(),
                (rootEntityId, _, _, _, _) => new ScriptAuthorityProjectionContext
                {
                    ProjectionId = $"{rootEntityId}:authority",
                    RootActorId = rootEntityId,
                },
                context => new ScriptAuthorityRuntimeLease(context)));
        services.TryAddSingleton<IProjectionPortReleaseService<ScriptAuthorityRuntimeLease>, ContextProjectionReleaseService<ScriptAuthorityRuntimeLease, ScriptAuthorityProjectionContext, IReadOnlyList<string>>>();
        services.TryAddSingleton<ScriptAuthorityProjectionPortService>();
        services.TryAddSingleton<IProjectionSessionEventCodec<ScriptEvolutionSessionCompletedEvent>, ScriptEvolutionSessionEventCodec>();
        services.TryAddSingleton<IProjectionSessionEventHub<ScriptEvolutionSessionCompletedEvent>, ProjectionSessionEventHub<ScriptEvolutionSessionCompletedEvent>>();
        services.AddEventSinkProjectionRuntimeCore<
            ScriptEvolutionSessionProjectionContext,
            IReadOnlyList<string>,
            ScriptEvolutionRuntimeLease,
            ScriptEvolutionSessionCompletedEvent>();
        services.TryAddSingleton<IProjectionPortActivationService<ScriptEvolutionRuntimeLease>>(sp =>
            new ContextProjectionActivationService<ScriptEvolutionRuntimeLease, ScriptEvolutionSessionProjectionContext, IReadOnlyList<string>>(
                sp.GetRequiredService<IProjectionLifecycleService<ScriptEvolutionSessionProjectionContext, IReadOnlyList<string>>>(),
                (rootEntityId, _, _, commandId, _) =>
                {
                    ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
                    return new ScriptEvolutionSessionProjectionContext
                    {
                        ProjectionId = rootEntityId,
                        RootActorId = rootEntityId,
                        ProposalId = commandId,
                    };
                },
                context => new ScriptEvolutionRuntimeLease(context)));
        services.TryAddSingleton<IProjectionPortReleaseService<ScriptEvolutionRuntimeLease>, ContextProjectionReleaseService<ScriptEvolutionRuntimeLease, ScriptEvolutionSessionProjectionContext, IReadOnlyList<string>>>();
        services.TryAddSingleton<ScriptEvolutionProjectionPortService>();
        services.TryAddSingleton<IScriptEvolutionProjectionPort>(sp =>
            sp.GetRequiredService<ScriptEvolutionProjectionPortService>());
        services.TryAddSingleton<IScriptEvolutionDecisionReadPort, ProjectionScriptEvolutionDecisionReadPort>();
        services.TryAddSingleton<ScriptReadModelQueryReader>();
        services.TryAddSingleton<IScriptReadModelQueryPort>(sp =>
            sp.GetRequiredService<ScriptReadModelQueryReader>());
        services.TryAddSingleton<IScriptDefinitionSnapshotPort, ProjectionScriptDefinitionSnapshotPort>();
        services.TryAddSingleton<IScriptCatalogQueryPort, ProjectionScriptCatalogQueryPort>();
        services.TryAddSingleton<IScriptAuthorityReadModelActivationPort, ProjectionScriptAuthorityReadModelActivationPort>();
        services.TryAddSingleton<IScriptReadModelMaterializationCompiler, ScriptReadModelMaterializationCompiler>();
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
            IProjectionProjector<ScriptExecutionProjectionContext, IReadOnlyList<string>>,
            ScriptReadModelProjector>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionProjector<ScriptExecutionProjectionContext, IReadOnlyList<string>>,
            ScriptNativeDocumentProjector>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionProjector<ScriptExecutionProjectionContext, IReadOnlyList<string>>,
            ScriptNativeGraphProjector>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionProjector<ScriptExecutionProjectionContext, IReadOnlyList<string>>,
            ScriptExecutionSessionEventProjector>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionProjector<ScriptAuthorityProjectionContext, IReadOnlyList<string>>,
            ScriptDefinitionSnapshotProjector>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionProjector<ScriptAuthorityProjectionContext, IReadOnlyList<string>>,
            ScriptCatalogEntryProjector>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionProjector<ScriptEvolutionSessionProjectionContext, IReadOnlyList<string>>,
            ScriptEvolutionReadModelProjector>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionProjector<ScriptEvolutionSessionProjectionContext, IReadOnlyList<string>>,
            ScriptEvolutionSessionCompletedEventProjector>());

        return services;
    }
}
