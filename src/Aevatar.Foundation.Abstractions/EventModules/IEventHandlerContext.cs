// ─────────────────────────────────────────────────────────────
// IEventHandlerContext - event handler execution context.
// Provides access to agent, services, logger, and event publishing APIs.
// ─────────────────────────────────────────────────────────────

using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace Aevatar.Foundation.Abstractions.EventModules;

/// <summary>
/// Event handler context exposed during event processing.
/// </summary>
public interface IEventHandlerContext
{
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
}