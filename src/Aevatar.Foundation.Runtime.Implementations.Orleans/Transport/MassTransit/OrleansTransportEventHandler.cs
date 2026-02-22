using Microsoft.Extensions.Logging;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.MassTransit;

public sealed class OrleansTransportEventHandler(
    IGrainFactory grainFactory,
    ILogger<OrleansTransportEventHandler> logger) : IOrleansTransportEventHandler
{
    public async Task<bool> HandleAsync(OrleansTransportEventMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (string.IsNullOrWhiteSpace(message.TargetActorId) ||
            message.EnvelopeBytes is not { Length: > 0 })
        {
            logger.LogWarning("Skip empty transport event message.");
            return false;
        }

        try
        {
            var grain = grainFactory.GetGrain<IRuntimeActorGrain>(message.TargetActorId);
            await grain.HandleEnvelopeAsync(message.EnvelopeBytes);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to route transport event to actor {TargetActorId}.",
                message.TargetActorId);
            return false;
        }
    }
}
