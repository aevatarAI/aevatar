using Aevatar.Foundation.Abstractions.Streaming;
using Google.Protobuf;

namespace Aevatar.Foundation.Runtime.Streaming;

public sealed class StreamProviderActorEventSubscriptionProvider : IActorEventSubscriptionProvider
{
    private readonly IStreamProvider _streams;

    public StreamProviderActorEventSubscriptionProvider(IStreamProvider streams)
    {
        _streams = streams ?? throw new ArgumentNullException(nameof(streams));
    }

    public Task<IAsyncDisposable> SubscribeAsync<TMessage>(
        string actorId,
        Func<TMessage, Task> handler,
        CancellationToken ct = default)
        where TMessage : class, IMessage, new()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(handler);
        return _streams.GetStream(actorId).SubscribeAsync(handler, ct);
    }
}
