namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;

internal interface IStreamCacheManager
{
    void RemoveStream(string actorId);
}
