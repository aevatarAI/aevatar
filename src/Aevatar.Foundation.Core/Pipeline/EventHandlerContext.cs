// ─────────────────────────────────────────────────────────────
// EventHandlerContext - event handler execution context.
// Implements IEventHandlerContext and provides agent, publisher, services, and logger.
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions.EventModules;
using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace Aevatar.Foundation.Core.Pipeline;

/// <summary>
/// Event handler execution context for current agent, publisher, services, and logger.
/// </summary>
internal sealed class EventHandlerContext : IEventHandlerContext
{
    private readonly IEventPublisher _publisher;

    /// <summary>Builds context with agent, publisher, services, and logger.</summary>
    public EventHandlerContext(IAgent agent, IEventPublisher publisher, IServiceProvider services, ILogger logger)
    {
        Agent = agent; _publisher = publisher; Services = services; Logger = logger;
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
        _publisher.PublishAsync(evt, direction, ct);
}
