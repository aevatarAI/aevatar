using Aevatar.Foundation.Abstractions;
using Google.Protobuf;

namespace Aevatar.Scripting.Core.Runtime;

public sealed class ScriptExecutionMessageContext
{
    private readonly IEventPublisher _publisher;
    private readonly EventEnvelope? _inboundEnvelope;

    public ScriptExecutionMessageContext(
        IEventPublisher publisher,
        EventEnvelope? inboundEnvelope)
    {
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _inboundEnvelope = inboundEnvelope?.Clone();
    }

    public Task PublishAsync(
        IMessage eventPayload,
        EventDirection direction,
        CancellationToken ct) =>
        _publisher.PublishAsync(eventPayload, direction, ct, _inboundEnvelope);

    public Task SendToAsync(
        string targetActorId,
        IMessage eventPayload,
        CancellationToken ct) =>
        _publisher.SendToAsync(targetActorId, eventPayload, ct, _inboundEnvelope);
}
