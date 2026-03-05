// ─────────────────────────────────────────────────────────────
// EventHandlerContext - event handler execution context.
// Implements IEventHandlerContext and provides agent, publisher, services, and logger.
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Runtime.Async;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Foundation.Core.Pipeline;

/// <summary>
/// Event handler execution context for current agent, publisher, services, and logger.
/// </summary>
internal sealed class EventHandlerContext : IEventHandlerContext
{
    private readonly IEventPublisher _publisher;
    private readonly IActorRuntimeAsyncScheduler _asyncScheduler;
    public EventEnvelope InboundEnvelope { get; }

    /// <summary>Builds context with agent, publisher, services, and logger.</summary>
    public EventHandlerContext(
        IAgent agent,
        IEventPublisher publisher,
        IActorRuntimeAsyncScheduler asyncScheduler,
        IServiceProvider services,
        ILogger logger,
        EventEnvelope inboundEnvelope)
    {
        Agent = agent;
        _publisher = publisher;
        _asyncScheduler = asyncScheduler ?? throw new ArgumentNullException(nameof(asyncScheduler));
        Services = services;
        Logger = logger;
        InboundEnvelope = inboundEnvelope;
    }

    /// <summary>Current agent ID.</summary>
    public string AgentId => Agent.Id;

    /// <summary>Current executing agent.</summary>
    public IAgent Agent { get; }

    /// <summary>Service provider.</summary>
    public IServiceProvider Services { get; }

    /// <summary>Logger.</summary>
    public ILogger Logger { get; }

    /// <summary>Publishes an event to stream routing.</summary>
    public Task PublishAsync<TEvent>(TEvent evt, EventDirection direction = EventDirection.Down,
        CancellationToken ct = default) where TEvent : IMessage =>
        _publisher.PublishAsync(evt, direction, ct, InboundEnvelope);

    /// <summary>Sends an event directly to the target actor.</summary>
    public Task SendToAsync<TEvent>(string targetActorId, TEvent evt,
        CancellationToken ct = default) where TEvent : IMessage =>
        _publisher.SendToAsync(targetActorId, evt, ct, InboundEnvelope);

    public Task<RuntimeCallbackLease> ScheduleSelfTimeoutAsync(
        string callbackId,
        TimeSpan dueTime,
        IMessage evt,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        return _asyncScheduler.ScheduleTimeoutAsync(
            new RuntimeTimeoutRequest
            {
                ActorId = AgentId,
                CallbackId = callbackId,
                TriggerEnvelope = BuildSelfEnvelope(evt, metadata),
                DueTime = dueTime,
            },
            ct);
    }

    public Task<RuntimeCallbackLease> ScheduleSelfTimerAsync(
        string callbackId,
        TimeSpan dueTime,
        TimeSpan period,
        IMessage evt,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        return _asyncScheduler.ScheduleTimerAsync(
            new RuntimeTimerRequest
            {
                ActorId = AgentId,
                CallbackId = callbackId,
                TriggerEnvelope = BuildSelfEnvelope(evt, metadata),
                DueTime = dueTime,
                Period = period,
            },
            ct);
    }

    public Task CancelScheduledCallbackAsync(
        string callbackId,
        long? expectedGeneration = null,
        CancellationToken ct = default)
    {
        return _asyncScheduler.CancelAsync(AgentId, callbackId, expectedGeneration, ct);
    }

    private EventEnvelope BuildSelfEnvelope(
        IMessage evt,
        IReadOnlyDictionary<string, string>? metadata)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            PublisherId = AgentId,
            Direction = EventDirection.Self,
            CorrelationId = InboundEnvelope.CorrelationId ?? string.Empty,
            TargetActorId = AgentId,
        };

        foreach (var pair in InboundEnvelope.Metadata)
            envelope.Metadata[pair.Key] = pair.Value;

        if (metadata != null)
        {
            foreach (var pair in metadata)
                envelope.Metadata[pair.Key] = pair.Value;
        }

        return envelope;
    }
}
