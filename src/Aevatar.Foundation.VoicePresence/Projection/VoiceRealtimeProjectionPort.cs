using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.VoicePresence.Abstractions;

namespace Aevatar.Foundation.VoicePresence.Projection;

public sealed class VoiceRealtimeProjectionPort
    : EventSinkProjectionLifecyclePortBase<IVoiceRealtimeProjectionLease, VoiceRealtimeRuntimeLease, VoiceRealtimeFrame>,
      IVoiceRealtimeProjectionPort
{
    public VoiceRealtimeProjectionPort(
        IProjectionScopeReleaseService<VoiceRealtimeRuntimeLease> releaseService,
        IProjectionSessionEventHub<VoiceRealtimeFrame> sessionEventHub)
        : base(static () => true, releaseService, sessionEventHub)
    {
    }
}
