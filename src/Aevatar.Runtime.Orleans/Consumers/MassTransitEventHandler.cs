// ─────────────────────────────────────────────────────────────
// MassTransitEventHandler - bridges MassTransit Consumer to
// Orleans Grain. The Consumer extracts targetActorId from the
// message and calls this handler, which routes to the Grain.
// ─────────────────────────────────────────────────────────────

using Aevatar.Orleans.Grains;
using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace Aevatar.Orleans.Consumers;

/// <summary>
/// Bridges MassTransit consumer messages to Orleans Grains.
/// This is the sole event entry point for all Grains.
/// </summary>
public sealed class MassTransitEventHandler : IMassTransitEventHandler
{
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<MassTransitEventHandler> _logger;

    /// <summary>Creates a MassTransit event handler.</summary>
    public MassTransitEventHandler(
        IGrainFactory grainFactory,
        ILogger<MassTransitEventHandler> logger)
    {
        _grainFactory = grainFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> HandleEventAsync(string agentId, EventEnvelope envelope)
    {
        try
        {
            var grain = _grainFactory.GetGrain<IGAgentGrain>(agentId);
            await grain.HandleEventAsync(envelope.ToByteArray());
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to route event {EventId} to Grain {AgentId}",
                envelope.Id, agentId);
            return false;
        }
    }
}
