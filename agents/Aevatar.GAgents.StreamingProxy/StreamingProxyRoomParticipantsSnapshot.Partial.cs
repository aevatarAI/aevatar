using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgents.StreamingProxy;

// Refactor (iter43/issue-865-streaming-proxy-room-chat-host-orchestration):
//   Old pattern: StreamingProxy chat endpoint and participant coordinator fetch runtime actor objects, run Nyx participant discussion loops, mutate participant side-store state, and dispatch room events from Host/Application-side orchestration.
//   New principle: StreamingProxyGAgent owns participant admission, reply rounds, leave/failure decisions, and terminal-state publication; Host submits one typed command and observes projection/readmodel events only. Coordinator is adapter-only for Nyx external calls.
public sealed partial class StreamingProxyRoomParticipantsSnapshot
    : IProjectionReadModel<StreamingProxyRoomParticipantsSnapshot>
{
    string IProjectionReadModel.ActorId => ActorId;

    long IProjectionReadModel.StateVersion => StateVersion;

    string IProjectionReadModel.LastEventId => LastEventId;

    DateTimeOffset IProjectionReadModel.UpdatedAt =>
        UpdatedAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue;
}
