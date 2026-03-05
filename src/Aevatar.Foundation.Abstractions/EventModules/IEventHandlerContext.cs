// ─────────────────────────────────────────────────────────────
// IEventHandlerContext - event handler execution context.
// Provides access to agent, services, logger, and event publishing APIs.
// ─────────────────────────────────────────────────────────────

using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Aevatar.Foundation.Abstractions.Runtime.Async;

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

    Task<RuntimeCallbackLease> ScheduleSelfTimeoutAsync(
        string callbackId,
        TimeSpan dueTime,
        IMessage evt,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default);

    Task<RuntimeCallbackLease> ScheduleSelfTimerAsync(
        string callbackId,
        TimeSpan dueTime,
        TimeSpan period,
        IMessage evt,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default);

    Task CancelScheduledCallbackAsync(
        string callbackId,
        long? expectedGeneration = null,
        CancellationToken ct = default);
}
