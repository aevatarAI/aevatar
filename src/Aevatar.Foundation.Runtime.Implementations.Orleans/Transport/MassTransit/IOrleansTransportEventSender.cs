namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.MassTransit;

public interface IOrleansTransportEventSender
{
    Task SendAsync(string targetActorId, EventEnvelope envelope, CancellationToken ct = default);
}
