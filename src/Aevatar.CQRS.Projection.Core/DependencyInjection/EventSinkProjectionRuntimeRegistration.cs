using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.CQRS.Projection.Core.DependencyInjection;

/// <summary>
/// Shared registration helpers for actorized session projection components.
/// </summary>
public static class EventSinkProjectionRuntimeRegistration
{
    public static IServiceCollection AddEventSinkProjectionRuntimeCore<TContext, TRuntimeLease, TEvent>(
        this IServiceCollection services)
        where TContext : class, IProjectionSessionContext
        where TRuntimeLease : EventSinkProjectionRuntimeLeaseBase<TEvent>, IProjectionPortSessionLease
        where TEvent : class
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }
}
