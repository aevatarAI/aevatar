using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Google.Protobuf;

namespace Aevatar.Workflow.Host.Api.Tests;

internal sealed class TestActorEventSubscriptionProvider(IStreamProvider streams) : IActorEventSubscriptionProvider
{
    public Task<IAsyncDisposable> SubscribeAsync<TMessage>(
        string actorId,
        Func<TMessage, Task> handler,
        CancellationToken ct = default)
        where TMessage : class, IMessage, new()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(handler);
        return streams.GetStream(actorId).SubscribeAsync(handler, ct);
    }
}
