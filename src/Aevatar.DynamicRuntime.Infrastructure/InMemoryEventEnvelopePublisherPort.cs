using System.Collections.Concurrent;
using Aevatar.DynamicRuntime.Abstractions.Contracts;

namespace Aevatar.DynamicRuntime.Infrastructure;

public sealed class InMemoryEventEnvelopePublisherPort : IEventEnvelopePublisherPort
{
    private readonly ConcurrentQueue<ScriptEventEnvelope> _published = new();

    public Task PublishAsync(ScriptEventEnvelope envelope, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _published.Enqueue(envelope);
        return Task.CompletedTask;
    }
}
