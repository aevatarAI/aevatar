// ─────────────────────────────────────────────────────────────
// EventHandlerContext - event handler execution context.
// Implements IEventHandlerContext and provides agent, publisher, services, and logger.
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace Aevatar.Foundation.Core.Pipeline;

/// <summary>
/// Event handler execution context for current agent, publisher, services, and logger.
/// </summary>
internal sealed class EventHandlerContext : IEventHandlerContext
{
    private readonly IEventPublisher _publisher;
    private readonly IActorRuntimeCallbackScheduler _callbackScheduler;
    public EventEnvelope InboundEnvelope { get; }

    /// <summary>Builds context with agent, publisher, services, and logger.</summary>
    public EventHandlerContext(
        IAgent agent,
        IEventPublisher publisher,
        IActorRuntimeCallbackScheduler callbackScheduler,
        IServiceProvider services,
        ILogger logger,
        EventEnvelope inboundEnvelope)
    {
        Agent = agent;
        _publisher = publisher;
        _callbackScheduler = callbackScheduler ?? throw new ArgumentNullException(nameof(callbackScheduler));
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
        return _callbackScheduler.ScheduleTimeoutAsync(
            new RuntimeCallbackTimeoutRequest
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
        return _callbackScheduler.ScheduleTimerAsync(
            new RuntimeCallbackTimerRequest
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
        RuntimeCallbackLease lease,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return _callbackScheduler.CancelAsync(lease, ct);
    }

    private EventEnvelope BuildSelfEnvelope(
        IMessage evt,
        IReadOnlyDictionary<string, string>? metadata) =>
        SelfEventEnvelopeFactory.Create(AgentId, evt, InboundEnvelope, metadata);
}
