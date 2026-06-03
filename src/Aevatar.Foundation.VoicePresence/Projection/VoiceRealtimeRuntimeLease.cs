using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.VoicePresence.Abstractions;

namespace Aevatar.Foundation.VoicePresence.Projection;

public sealed class VoiceRealtimeRuntimeLease
    : EventSinkProjectionRuntimeLeaseBase<VoiceRealtimeFrame>,
      IVoiceRealtimeProjectionLease,
      IProjectionContextRuntimeLease<VoiceRealtimeProjectionContext>
{
    public VoiceRealtimeRuntimeLease(VoiceRealtimeProjectionContext context)
        : base(context?.RootActorId ?? throw new ArgumentNullException(nameof(context)))
    {
        Context = context;
        SessionId = context.SessionId;
    }

    public VoiceRealtimeProjectionContext Context { get; }

    public string ActorId => RootEntityId;

    public string SessionId { get; }
}
