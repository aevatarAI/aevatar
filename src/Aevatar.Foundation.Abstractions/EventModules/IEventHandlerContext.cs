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
{
    /// <summary>Raw inbound envelope being handled.</summary>
    EventEnvelope InboundEnvelope { get; }

    /// <summary>Current agent ID.</summary>
    string AgentId { get; }

    /// <summary>Current agent instance.</summary>
    IAgent Agent { get; }

    /// <summary>Service provider used for dependency resolution.</summary>
    IServiceProvider Services { get; }

    /// <summary>Logger.</summary>
    ILogger Logger { get; }

    /// <summary>Publishes an event with the specified direction.</summary>
    /// <typeparam name="TEvent">Event type, must implement Protobuf IMessage.</typeparam>
    Task PublishAsync<TEvent>(TEvent evt, EventDirection direction = EventDirection.Down,
        CancellationToken ct = default) where TEvent : IMessage;

    /// <summary>Sends an event directly to a target actor.</summary>
    /// <typeparam name="TEvent">Event type, must implement Protobuf IMessage.</typeparam>
    Task SendToAsync<TEvent>(string targetActorId, TEvent evt,
        CancellationToken ct = default) where TEvent : IMessage
    {
        throw new NotSupportedException(
            $"{GetType().Name} does not support SendToAsync.");
    }

    /// <summary>Schedules a durable self timeout callback.</summary>
    Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
        string callbackId,
        TimeSpan dueTime,
        IMessage evt,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default);

    /// <summary>Schedules a durable self timer callback.</summary>
    Task<RuntimeCallbackLease> ScheduleSelfDurableTimerAsync(
        string callbackId,
        TimeSpan dueTime,
        TimeSpan period,
        IMessage evt,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default);

    /// <summary>Cancels a durable callback using lease/CAS semantics.</summary>
    Task CancelDurableCallbackAsync(
        RuntimeCallbackLease lease,
        CancellationToken ct = default);
}
