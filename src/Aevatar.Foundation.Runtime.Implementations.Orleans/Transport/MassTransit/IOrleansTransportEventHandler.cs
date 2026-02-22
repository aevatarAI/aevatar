namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.MassTransit;

public interface IOrleansTransportEventHandler
{
    Task<bool> HandleAsync(OrleansTransportEventMessage message, CancellationToken ct = default);
}
