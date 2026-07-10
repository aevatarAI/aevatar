using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;

namespace Aevatar.GAgentService.Abstractions.Ports;

public interface ILlmSessionObservationProjectionLease
{
    string ActorId { get; }

    string ResponseId { get; }
}

public interface ILlmSessionObservationProjectionPort
    : IEventSinkProjectionLifecyclePort<ILlmSessionObservationProjectionLease, EventEnvelope>
{
    Task<EventSinkProjectionAttachment<ILlmSessionObservationProjectionLease>?> AttachExistingResponseProjectionAsync(
        string actorId,
        string responseId,
        IEventSink<EventEnvelope> sink,
        CancellationToken ct = default);
}
