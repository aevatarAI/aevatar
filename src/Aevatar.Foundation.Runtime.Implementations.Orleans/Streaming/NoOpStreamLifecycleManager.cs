namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;

internal sealed class NoOpStreamLifecycleManager : IStreamLifecycleManager
{
    public void RemoveStream(string actorId)
    {
        _ = actorId;
    }
}
