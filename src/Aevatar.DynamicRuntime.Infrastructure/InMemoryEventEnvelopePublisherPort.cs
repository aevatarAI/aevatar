using Aevatar.DynamicRuntime.Abstractions.Contracts;

namespace Aevatar.DynamicRuntime.Infrastructure;

public sealed class InMemoryEventEnvelopePublisherPort : IEventEnvelopePublisherPort
{
    private readonly InMemoryEventEnvelopeBusState _state;

    public InMemoryEventEnvelopePublisherPort(InMemoryEventEnvelopeBusState state)
    {
        _state = state;
    }

    public Task PublishAsync(ScriptEventEnvelope envelope, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var sequence = _state.NextSequence();
        _state.Envelopes[sequence] = envelope;
        return Task.CompletedTask;
    }
}
