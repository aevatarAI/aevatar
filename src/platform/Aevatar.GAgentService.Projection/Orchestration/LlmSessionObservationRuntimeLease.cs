using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class LlmSessionObservationRuntimeLease
    : EventSinkProjectionRuntimeLeaseBase<EventEnvelope>,
      ILlmSessionObservationProjectionLease,
      IProjectionContextRuntimeLease<LlmSessionObservationProjectionContext>
{
    public LlmSessionObservationRuntimeLease(LlmSessionObservationProjectionContext context)
        : base(context?.RootActorId ?? throw new ArgumentNullException(nameof(context)))
    {
        Context = context;
        ResponseId = context.SessionId;
    }

    public string ActorId => RootEntityId;

    public string ResponseId { get; }

    public LlmSessionObservationProjectionContext Context { get; }
}
