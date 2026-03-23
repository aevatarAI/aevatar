using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.CQRS.Projection.Core.DependencyInjection;

/// <summary>
/// Shared registration helpers for actorized session projection components.
/// </summary>
public static class EventSinkProjectionRuntimeRegistration
{
    public static IServiceCollection AddEventSinkProjectionRuntimeCore<TContext, TRuntimeLease, TEvent, TScopeAgent>(
        this IServiceCollection services,
        Func<ProjectionRuntimeScopeKey, TContext> contextFactory,
        Func<TContext, TRuntimeLease> leaseFactory)
        where TContext : class, IProjectionSessionContext
        where TRuntimeLease : EventSinkProjectionRuntimeLeaseBase<TEvent>, IProjectionPortSessionLease, IProjectionRuntimeLease, IProjectionContextRuntimeLease<TContext>
        where TEvent : class
        where TScopeAgent : IAgent
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(leaseFactory);

        services.TryAddSingleton<IProjectionFailureReplayService, ProjectionFailureReplayService>();
        services.TryAddSingleton<IProjectionFailureAlertSink, LoggingProjectionFailureAlertSink>();
        services.TryAddSingleton<Func<ProjectionRuntimeScopeKey, TContext>>(_ => contextFactory);
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
                sp.GetService<Aevatar.Foundation.Abstractions.TypeSystem.IAgentTypeVerifier>()));
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
                sp.GetService<Aevatar.Foundation.Abstractions.TypeSystem.IAgentTypeVerifier>()));
        return services;
    }
}
