// ─────────────────────────────────────────────────────────────
// IEventHandlerContext - event handler execution context.
// Provides access to agent, services, logger, and event publishing APIs.
// ─────────────────────────────────────────────────────────────

using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;

namespace Aevatar.Foundation.Abstractions.EventModules;

/// <summary>
/// Event handler context exposed during event processing.
/// </summary>
public interface IEventHandlerContext
    : IEventContext
{
    /// <summary>Current agent instance.</summary>
    IAgent Agent { get; }

    /// <summary>Schedules a durable self timeout callback.</summary>
    Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
        string callbackId,
        TimeSpan dueTime,
        IMessage evt,
        EventEnvelopePublishOptions? options = null,
        CancellationToken ct = default);

    /// <summary>Schedules a durable self timer callback.</summary>
    Task<RuntimeCallbackLease> ScheduleSelfDurableTimerAsync(
        string callbackId,
        TimeSpan dueTime,
        TimeSpan period,
        IMessage evt,
        EventEnvelopePublishOptions? options = null,
        CancellationToken ct = default);

    /// <summary>Cancels a durable callback using lease/CAS semantics.</summary>
    Task CancelDurableCallbackAsync(
        RuntimeCallbackLease lease,
        CancellationToken ct = default);
}
