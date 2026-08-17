using Aevatar.CQRS.Core.Abstractions.Streaming;
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
/// Shared registration helpers for actorized session projection components.
/// </summary>
// Refactor (iter367/cluster-issue377): Old pattern: registration required IProjectionPortSessionLease.
// Refactor (iter367/cluster-issue377): Old pattern: the alias duplicated Context.RootActorId and Context.SessionId.
// Refactor (iter367/cluster-issue377): New principle: runtime registration depends on typed session context.
// Refactor (iter367/cluster-issue377): New principle: session identity is read from Context at lifecycle boundaries.
public static class EventSinkProjectionRuntimeRegistration
{
    // Refactor (iter367/cluster-issue377): Old pattern: session runtimes had to implement an alias lease contract.
    // Refactor (iter367/cluster-issue377): Old pattern: DI accepted duplicate ScopeId + SessionId surfaces.
    // Refactor (iter367/cluster-issue377): New principle: TRuntimeLease only needs runtime lease and context lease contracts.
    // Refactor (iter367/cluster-issue377): New principle: the context factory is the single source for RootActorId/session.
    public static IServiceCollection AddEventSinkProjectionRuntimeCore<TContext, TRuntimeLease, TEvent, TScopeAgent>(
        this IServiceCollection services,
        Func<ProjectionRuntimeScopeKey, TContext> contextFactory,
        Func<TContext, TRuntimeLease> leaseFactory)
        where TContext : class, IProjectionSessionContext
        where TRuntimeLease : EventSinkProjectionRuntimeLeaseBase<TEvent>, IProjectionRuntimeLease, IProjectionContextRuntimeLease<TContext>
        where TEvent : class
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
                    ProjectionRuntimeMode.SessionObservation,
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
                    ProjectionRuntimeMode.SessionObservation,
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
                    ProjectionRuntimeMode.SessionObservation,
                    lease.Context.SessionId),
                sp.GetService<Aevatar.Foundation.Abstractions.TypeSystem.IAgentKindVerifier>(),
                sp.GetRequiredService<Aevatar.Foundation.Abstractions.TypeSystem.IAgentKindRegistry>(),
                sp.GetRequiredService<IStreamForwardingBindingAuthority>(),
                sp.GetRequiredService<IStreamForwardingRegistry>()));
        return services;
    }
}
