using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.VoicePresence.Abstractions;

namespace Aevatar.Foundation.VoicePresence.Projection;

public interface IVoiceRealtimeProjectionPort
    : IEventSinkProjectionLifecyclePort<IVoiceRealtimeProjectionLease, VoiceRealtimeFrame>
{
}

public interface IVoiceRealtimeProjectionLease
{
    string ActorId { get; }

    string SessionId { get; }
}
