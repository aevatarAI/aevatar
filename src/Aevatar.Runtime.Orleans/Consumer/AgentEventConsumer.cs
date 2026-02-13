// ─────────────────────────────────────────────────────────────
// AgentEventConsumer - MassTransit consumer for AgentEventMessage.
// Deserializes the envelope, extracts targetActorId, and delegates
// to IMassTransitEventHandler for Grain routing.
// ─────────────────────────────────────────────────────────────

using MassTransit;
using Microsoft.Extensions.Logging;

namespace Aevatar.Orleans.Consumer;

/// <summary>
/// MassTransit consumer that receives AgentEventMessage and routes
/// to the correct Orleans Grain via IMassTransitEventHandler.
/// </summary>
public sealed class AgentEventConsumer : IConsumer<AgentEventMessage>
{
    private readonly IMassTransitEventHandler _handler;
    private readonly ILogger<AgentEventConsumer> _logger;

    /// <summary>Creates the consumer.</summary>
    public AgentEventConsumer(
        IMassTransitEventHandler handler,
        ILogger<AgentEventConsumer> logger)
    {
        _handler = handler;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Consume(ConsumeContext<AgentEventMessage> context)
    {
        var msg = context.Message;
        if (string.IsNullOrEmpty(msg.TargetActorId) || msg.EnvelopeBytes.Length == 0)
        {
            _logger.LogWarning("Received empty AgentEventMessage, skipping");
            return;
        }

        EventEnvelope envelope;
        try
        {
            envelope = EventEnvelope.Parser.ParseFrom(msg.EnvelopeBytes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse EventEnvelope for {TargetId}", msg.TargetActorId);
            return;
        }

        await _handler.HandleEventAsync(msg.TargetActorId, envelope);
    }
}
