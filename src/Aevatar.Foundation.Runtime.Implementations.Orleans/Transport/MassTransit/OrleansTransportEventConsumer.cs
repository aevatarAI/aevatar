using MassTransit;
using Microsoft.Extensions.Logging;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.MassTransit;

public sealed class OrleansTransportEventConsumer(
    IOrleansTransportEventHandler handler,
    ILogger<OrleansTransportEventConsumer> logger) : IConsumer<OrleansTransportEventMessage>
{
    public async Task Consume(ConsumeContext<OrleansTransportEventMessage> context)
    {
        var handled = await handler.HandleAsync(context.Message, context.CancellationToken);
        if (!handled)
        {
            logger.LogWarning("Orleans transport event was not handled for target {TargetActorId}.", context.Message.TargetActorId);
        }
    }
}
