using Aevatar.Foundation.Abstractions;

namespace Aevatar.Foundation.Runtime.Streaming.Implementations.MassTransit;

internal sealed class MassTransitStreamLifecycleManager(MassTransitStreamProvider streamProvider) : IStreamLifecycleManager
{
    public void RemoveStream(string actorId)
    {
        streamProvider.RemoveStream(actorId);
    }
}
