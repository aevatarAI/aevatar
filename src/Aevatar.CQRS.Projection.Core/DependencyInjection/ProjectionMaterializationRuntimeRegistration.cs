using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Core.TypeSystem;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Aevatar.CQRS.Projection.Core.DependencyInjection;

/// <summary>
/// Shared registration helpers for actorized durable materialization components.
/// </summary>
public static class ProjectionMaterializationRuntimeRegistration
{
    public static IServiceCollection AddProjectionMaterializationRuntimeCore<TContext, TRuntimeLease, TScopeAgent>(
        this IServiceCollection services,
        Func<ProjectionRuntimeScopeKey, TContext> contextFactory,
        Func<TContext, TRuntimeLease> leaseFactory)
        where TContext : class, IProjectionMaterializationContext
        where TRuntimeLease : class, IProjectionRuntimeLease, IProjectionContextRuntimeLease<TContext>
        where TScopeAgent : IAgent
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(leaseFactory);

        services.AddAevatarAgentKindRegistry(builder =>
            builder.Register(ProjectionScopeAgentRegistration.Create<TScopeAgent>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            ICommittedStatePublicationHook,
            ProjectionScopeCommittedStateRedactionHook>());
        services.TryAddSingleton<IProjectionFailureReplayService, ProjectionFailureReplayService>();
        services.TryAddSingleton<IProjectionFailureAlertSink, LoggingProjectionFailureAlertSink>();
        services.TryAddSingleton<Func<ProjectionRuntimeScopeKey, TContext>>(_ => contextFactory);
        services.TryAddSingleton<IProjectionScopeAttachExistingLeaseLookup<TRuntimeLease>>(sp =>
            new ProjectionScopeAttachExistingLeaseLookup<
                TRuntimeLease,
                TContext>(
                sp.GetRequiredService<IActorRuntime>(),
                request => contextFactory(new ProjectionRuntimeScopeKey(
                    request.RootActorId,
                    request.ProjectionKind,
                    ProjectionRuntimeMode.DurableMaterialization,
                    request.SessionId)),
                (_, context) => leaseFactory(context)));
        services.TryAddSingleton<IProjectionScopeActivationService<TRuntimeLease>>(sp =>
            new ProjectionScopeActivationService<
                    TRuntimeLease,
                    TContext,
                    TScopeAgent>(
                sp.GetRequiredService<IActorRuntime>(),
                sp.GetRequiredService<IActorDispatchPort>(),
                request => contextFactory(new ProjectionRuntimeScopeKey(
                    request.RootActorId,
                    request.ProjectionKind,
                    ProjectionRuntimeMode.DurableMaterialization,
                    request.SessionId)),
                (_, context) => leaseFactory(context),
                sp.GetService<Aevatar.Foundation.Abstractions.TypeSystem.IAgentKindVerifier>(),
                sp.GetRequiredService<Aevatar.Foundation.Abstractions.TypeSystem.IAgentKindRegistry>(),
                sp.GetService<IStreamPubSubMaintenance>(),
                sp.GetService<ILoggerFactory>(),
                sp.GetRequiredService<IStreamForwardingBindingAuthority>(),
                sp.GetRequiredService<IStreamForwardingRegistry>()));
        services.TryAddSingleton<IProjectionScopeReleaseService<TRuntimeLease>>(sp =>
            new ProjectionScopeReleaseService<
                TRuntimeLease,
                TScopeAgent>(
                sp.GetRequiredService<IActorRuntime>(),
                sp.GetRequiredService<IActorDispatchPort>(),
                lease => new ProjectionRuntimeScopeKey(
                    lease.Context.RootActorId,
                    lease.Context.ProjectionKind,
                    ProjectionRuntimeMode.DurableMaterialization,
                    lease.Context is IProjectionSessionScopedMaterializationContext scopedContext
                        ? scopedContext.SessionId
                        : string.Empty),
                sp.GetService<Aevatar.Foundation.Abstractions.TypeSystem.IAgentKindVerifier>(),
                sp.GetRequiredService<Aevatar.Foundation.Abstractions.TypeSystem.IAgentKindRegistry>(),
                sp.GetRequiredService<IStreamForwardingBindingAuthority>(),
                sp.GetRequiredService<IStreamForwardingRegistry>()));
        return services;
    }
}
