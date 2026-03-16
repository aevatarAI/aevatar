using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.CQRS.Projection.Core.DependencyInjection;

/// <summary>
/// Shared registration helpers for event-sink projection runtime components.
/// </summary>
public static class EventSinkProjectionRuntimeRegistration
{
    public static IServiceCollection AddEventSinkProjectionRuntimeCore<TContext, TRuntimeLease, TEvent>(
        this IServiceCollection services)
        where TContext : class, IProjectionSessionContext
        where TRuntimeLease : EventSinkProjectionRuntimeLeaseBase<TEvent>, IProjectionPortSessionLease, IProjectionContextRuntimeLease<TContext>, IProjectionStreamSubscriptionRuntimeLease
        where TEvent : class
    {
        return services.AddEventSinkProjectionRuntimeCore<
            TContext,
            TRuntimeLease,
            TEvent,
            DefaultEventSinkProjectionFailurePolicy<TRuntimeLease, TEvent>>();
    }

    public static IServiceCollection AddEventSinkProjectionRuntimeCore<TContext, TRuntimeLease, TEvent, TSinkFailurePolicy>(
        this IServiceCollection services)
        where TContext : class, IProjectionSessionContext
        where TRuntimeLease : EventSinkProjectionRuntimeLeaseBase<TEvent>, IProjectionPortSessionLease, IProjectionContextRuntimeLease<TContext>, IProjectionStreamSubscriptionRuntimeLease
        where TEvent : class
        where TSinkFailurePolicy : class, IEventSinkProjectionFailurePolicy<TRuntimeLease, TEvent>
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IProjectionCoordinator<TContext>, ProjectionCoordinator<TContext>>();
        services.TryAddSingleton<IProjectionDispatcher<TContext>, ProjectionDispatcher<TContext>>();
        services.TryAddSingleton<IProjectionSubscriptionRegistry<TContext, TRuntimeLease>, ProjectionSubscriptionRegistry<TContext, TRuntimeLease>>();
        services.TryAddSingleton<IProjectionLifecycleService<TContext, TRuntimeLease>, ProjectionLifecycleService<TContext, TRuntimeLease>>();
        services.TryAddSingleton<IEventSinkProjectionSubscriptionManager<TRuntimeLease, TEvent>,
            EventSinkProjectionSessionSubscriptionManager<TRuntimeLease, TEvent>>();
        services.TryAddSingleton<IEventSinkProjectionFailurePolicy<TRuntimeLease, TEvent>, TSinkFailurePolicy>();
        services.TryAddSingleton<IEventSinkProjectionLiveForwarder<TRuntimeLease, TEvent>,
            EventSinkProjectionLiveForwarder<TRuntimeLease, TEvent>>();

        return services;
    }
}
