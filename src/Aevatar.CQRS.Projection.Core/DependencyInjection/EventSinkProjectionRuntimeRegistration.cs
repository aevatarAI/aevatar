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
    public static IServiceCollection AddEventSinkProjectionRuntimeCore<TContext, TTopology, TRuntimeLease, TEvent>(
        this IServiceCollection services)
        where TContext : class, IProjectionContext, IProjectionStreamSubscriptionContext
        where TRuntimeLease : EventSinkProjectionRuntimeLeaseBase<TEvent>, IProjectionPortSessionLease
        where TEvent : class
    {
        return services.AddEventSinkProjectionRuntimeCore<
            TContext,
            TTopology,
            TRuntimeLease,
            TEvent,
            DefaultEventSinkProjectionFailurePolicy<TRuntimeLease, TEvent>>();
    }

    public static IServiceCollection AddEventSinkProjectionRuntimeCore<TContext, TTopology, TRuntimeLease, TEvent, TSinkFailurePolicy>(
        this IServiceCollection services)
        where TContext : class, IProjectionContext, IProjectionStreamSubscriptionContext
        where TRuntimeLease : EventSinkProjectionRuntimeLeaseBase<TEvent>, IProjectionPortSessionLease
        where TEvent : class
        where TSinkFailurePolicy : class, IEventSinkProjectionFailurePolicy<TRuntimeLease, TEvent>
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IProjectionCoordinator<TContext, TTopology>, ProjectionCoordinator<TContext, TTopology>>();
        services.TryAddSingleton<IProjectionDispatcher<TContext>, ProjectionDispatcher<TContext, TTopology>>();
        services.TryAddSingleton<IProjectionSubscriptionRegistry<TContext>, ProjectionSubscriptionRegistry<TContext>>();
        services.TryAddSingleton<IProjectionLifecycleService<TContext, TTopology>, ProjectionLifecycleService<TContext, TTopology>>();
        services.TryAddSingleton<IEventSinkProjectionSubscriptionManager<TRuntimeLease, TEvent>,
            EventSinkProjectionSessionSubscriptionManager<TRuntimeLease, TEvent>>();
        services.TryAddSingleton<IEventSinkProjectionFailurePolicy<TRuntimeLease, TEvent>, TSinkFailurePolicy>();
        services.TryAddSingleton<IEventSinkProjectionLiveForwarder<TRuntimeLease, TEvent>,
            EventSinkProjectionLiveForwarder<TRuntimeLease, TEvent>>();

        return services;
    }
}
