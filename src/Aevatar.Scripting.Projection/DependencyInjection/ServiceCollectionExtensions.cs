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
            ScriptExecutionMaterializationRuntimeLease>();
        services.AddEventSinkProjectionRuntimeCore<
            ScriptExecutionProjectionContext,
            ScriptExecutionRuntimeLease,
            EventEnvelope>();
        services.AddProjectionMaterializationRuntimeCore<
            ScriptAuthorityProjectionContext,
            ScriptAuthorityRuntimeLease>();
        services.TryAddSingleton<IProjectionScopeContextFactory<ScriptExecutionProjectionContext>>(
            _ => new ProjectionScopeContextFactory<ScriptExecutionProjectionContext>(scopeKey =>
                new ScriptExecutionProjectionContext
                {
                    SessionId = scopeKey.SessionId,
                    RootActorId = scopeKey.RootActorId,
                    ProjectionKind = scopeKey.ProjectionKind,
                }));
        services.TryAddSingleton<IProjectionScopeContextFactory<ScriptExecutionMaterializationContext>>(
            _ => new ProjectionScopeContextFactory<ScriptExecutionMaterializationContext>(scopeKey =>
                new ScriptExecutionMaterializationContext
                {
                    RootActorId = scopeKey.RootActorId,
                    ProjectionKind = scopeKey.ProjectionKind,
                }));
        services.TryAddSingleton<IProjectionScopeContextFactory<ScriptAuthorityProjectionContext>>(
            _ => new ProjectionScopeContextFactory<ScriptAuthorityProjectionContext>(scopeKey =>
                new ScriptAuthorityProjectionContext
                {
                    RootActorId = scopeKey.RootActorId,
                    ProjectionKind = scopeKey.ProjectionKind,
                }));
        services.TryAddSingleton<IProjectionScopeContextFactory<ScriptEvolutionSessionProjectionContext>>(
            _ => new ProjectionScopeContextFactory<ScriptEvolutionSessionProjectionContext>(scopeKey =>
                new ScriptEvolutionSessionProjectionContext
                {
                    SessionId = scopeKey.SessionId,
                    RootActorId = scopeKey.RootActorId,
                    ProjectionKind = scopeKey.ProjectionKind,
                }));
        services.TryAddSingleton<IProjectionScopeContextFactory<ScriptEvolutionMaterializationContext>>(
            _ => new ProjectionScopeContextFactory<ScriptEvolutionMaterializationContext>(scopeKey =>
                new ScriptEvolutionMaterializationContext
                {
                    RootActorId = scopeKey.RootActorId,
                    ProjectionKind = scopeKey.ProjectionKind,
                }));
        services.TryAddSingleton<IProjectionSessionActivationService<ScriptExecutionRuntimeLease>>(sp =>
            new ProjectionSessionScopeActivationService<
                ScriptExecutionRuntimeLease,
                ScriptExecutionProjectionContext,
                ProjectionSessionScopeGAgent<ScriptExecutionProjectionContext>>(
                sp.GetRequiredService<IActorRuntime>(),
                sp.GetRequiredService<IActorDispatchPort>(),
                request => new ScriptExecutionProjectionContext
                {
                    SessionId = request.SessionId,
                    RootActorId = request.RootActorId,
                    ProjectionKind = request.ProjectionKind,
                },
                static (_, context) => new ScriptExecutionRuntimeLease(context),
                sp.GetService<Aevatar.Foundation.Abstractions.TypeSystem.IAgentTypeVerifier>()));
        services.TryAddSingleton<IProjectionSessionReleaseService<ScriptExecutionRuntimeLease>>(sp =>
            new ProjectionSessionScopeReleaseService<
                ScriptExecutionRuntimeLease,
                ProjectionSessionScopeGAgent<ScriptExecutionProjectionContext>>(
                sp.GetRequiredService<IActorRuntime>(),
                sp.GetRequiredService<IActorDispatchPort>(),
                lease => new ProjectionRuntimeScopeKey(
                    lease.Context.RootActorId,
                    lease.Context.ProjectionKind,
                    ProjectionRuntimeMode.SessionObservation,
                    lease.Context.SessionId),
                sp.GetService<Aevatar.Foundation.Abstractions.TypeSystem.IAgentTypeVerifier>()));
        services.TryAddSingleton<ScriptExecutionProjectionPort>();
        services.TryAddSingleton<IScriptExecutionProjectionPort>(sp =>
            sp.GetRequiredService<ScriptExecutionProjectionPort>());
        services.TryAddSingleton<IProjectionMaterializationActivationService<ScriptExecutionMaterializationRuntimeLease>>(sp =>
            new ProjectionMaterializationScopeActivationService<
                ScriptExecutionMaterializationRuntimeLease,
                ScriptExecutionMaterializationContext,
                ProjectionMaterializationScopeGAgent<ScriptExecutionMaterializationContext>>(
                sp.GetRequiredService<IActorRuntime>(),
                sp.GetRequiredService<IActorDispatchPort>(),
                request => new ScriptExecutionMaterializationContext
                {
                    RootActorId = request.RootActorId,
                    ProjectionKind = request.ProjectionKind,
                },
                static (_, context) => new ScriptExecutionMaterializationRuntimeLease(context),
                sp.GetService<Aevatar.Foundation.Abstractions.TypeSystem.IAgentTypeVerifier>()));
        services.TryAddSingleton<IProjectionMaterializationReleaseService<ScriptExecutionMaterializationRuntimeLease>>(sp =>
            new ProjectionMaterializationScopeReleaseService<
                ScriptExecutionMaterializationRuntimeLease,
                ProjectionMaterializationScopeGAgent<ScriptExecutionMaterializationContext>>(
                sp.GetRequiredService<IActorRuntime>(),
                sp.GetRequiredService<IActorDispatchPort>(),
                lease => new ProjectionRuntimeScopeKey(
                    lease.Context.RootActorId,
                    lease.Context.ProjectionKind,
                    ProjectionRuntimeMode.DurableMaterialization),
                sp.GetService<Aevatar.Foundation.Abstractions.TypeSystem.IAgentTypeVerifier>()));
        services.TryAddSingleton<ScriptExecutionReadModelPort>();
        services.TryAddSingleton<IScriptExecutionReadModelActivationPort>(sp =>
            sp.GetRequiredService<ScriptExecutionReadModelPort>());
        services.TryAddSingleton<IProjectionMaterializationActivationService<ScriptAuthorityRuntimeLease>>(sp =>
            new ProjectionMaterializationScopeActivationService<
                ScriptAuthorityRuntimeLease,
                ScriptAuthorityProjectionContext,
                ProjectionMaterializationScopeGAgent<ScriptAuthorityProjectionContext>>(
                sp.GetRequiredService<IActorRuntime>(),
                sp.GetRequiredService<IActorDispatchPort>(),
                request => new ScriptAuthorityProjectionContext
                {
                    RootActorId = request.RootActorId,
                    ProjectionKind = request.ProjectionKind,
                },
                static (_, context) => new ScriptAuthorityRuntimeLease(context),
                sp.GetService<Aevatar.Foundation.Abstractions.TypeSystem.IAgentTypeVerifier>()));
        services.TryAddSingleton<IProjectionMaterializationReleaseService<ScriptAuthorityRuntimeLease>>(sp =>
            new ProjectionMaterializationScopeReleaseService<
                ScriptAuthorityRuntimeLease,
                ProjectionMaterializationScopeGAgent<ScriptAuthorityProjectionContext>>(
                sp.GetRequiredService<IActorRuntime>(),
                sp.GetRequiredService<IActorDispatchPort>(),
                lease => new ProjectionRuntimeScopeKey(
                    lease.Context.RootActorId,
                    lease.Context.ProjectionKind,
                    ProjectionRuntimeMode.DurableMaterialization),
                sp.GetService<Aevatar.Foundation.Abstractions.TypeSystem.IAgentTypeVerifier>()));
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
            new ProjectionSessionScopeActivationService<
                ScriptEvolutionRuntimeLease,
                ScriptEvolutionSessionProjectionContext,
                ProjectionSessionScopeGAgent<ScriptEvolutionSessionProjectionContext>>(
                sp.GetRequiredService<IActorRuntime>(),
                sp.GetRequiredService<IActorDispatchPort>(),
                request => new ScriptEvolutionSessionProjectionContext
                {
                    SessionId = request.SessionId,
                    RootActorId = request.RootActorId,
                    ProjectionKind = request.ProjectionKind,
                },
                static (_, context) => new ScriptEvolutionRuntimeLease(context),
                sp.GetService<Aevatar.Foundation.Abstractions.TypeSystem.IAgentTypeVerifier>()));
        services.TryAddSingleton<IProjectionSessionReleaseService<ScriptEvolutionRuntimeLease>>(sp =>
            new ProjectionSessionScopeReleaseService<
                ScriptEvolutionRuntimeLease,
                ProjectionSessionScopeGAgent<ScriptEvolutionSessionProjectionContext>>(
                sp.GetRequiredService<IActorRuntime>(),
                sp.GetRequiredService<IActorDispatchPort>(),
                lease => new ProjectionRuntimeScopeKey(
                    lease.Context.RootActorId,
                    lease.Context.ProjectionKind,
                    ProjectionRuntimeMode.SessionObservation,
                    lease.Context.SessionId),
                sp.GetService<Aevatar.Foundation.Abstractions.TypeSystem.IAgentTypeVerifier>()));
        services.TryAddSingleton<ScriptEvolutionProjectionPort>();
        services.TryAddSingleton<IScriptEvolutionProjectionPort>(sp =>
            sp.GetRequiredService<ScriptEvolutionProjectionPort>());
        services.TryAddSingleton<IProjectionMaterializationActivationService<ScriptEvolutionMaterializationRuntimeLease>>(sp =>
            new ProjectionMaterializationScopeActivationService<
                ScriptEvolutionMaterializationRuntimeLease,
                ScriptEvolutionMaterializationContext,
                ProjectionMaterializationScopeGAgent<ScriptEvolutionMaterializationContext>>(
                sp.GetRequiredService<IActorRuntime>(),
                sp.GetRequiredService<IActorDispatchPort>(),
                request => new ScriptEvolutionMaterializationContext
                {
                    RootActorId = request.RootActorId,
                    ProjectionKind = request.ProjectionKind,
                },
                static (_, context) => new ScriptEvolutionMaterializationRuntimeLease(context),
                sp.GetService<Aevatar.Foundation.Abstractions.TypeSystem.IAgentTypeVerifier>()));
        services.TryAddSingleton<IProjectionMaterializationReleaseService<ScriptEvolutionMaterializationRuntimeLease>>(sp =>
            new ProjectionMaterializationScopeReleaseService<
                ScriptEvolutionMaterializationRuntimeLease,
                ProjectionMaterializationScopeGAgent<ScriptEvolutionMaterializationContext>>(
                sp.GetRequiredService<IActorRuntime>(),
                sp.GetRequiredService<IActorDispatchPort>(),
                lease => new ProjectionRuntimeScopeKey(
                    lease.Context.RootActorId,
                    lease.Context.ProjectionKind,
                    ProjectionRuntimeMode.DurableMaterialization),
                sp.GetService<Aevatar.Foundation.Abstractions.TypeSystem.IAgentTypeVerifier>()));
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
