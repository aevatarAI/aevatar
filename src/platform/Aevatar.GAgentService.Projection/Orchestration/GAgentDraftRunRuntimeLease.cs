using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.Presentation.AGUI;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class GAgentDraftRunRuntimeLease
    : EventSinkProjectionRuntimeLeaseBase<AGUIEvent>,
      IGAgentDraftRunProjectionLease,
      IProjectionPortSessionLease,
      IProjectionContextRuntimeLease<GAgentDraftRunProjectionContext>
{
    public GAgentDraftRunRuntimeLease(GAgentDraftRunProjectionContext context)
        : base(context?.RootActorId ?? throw new ArgumentNullException(nameof(context)))
    {
        Context = context;
        CommandId = context.SessionId;
    }

    public string ActorId => RootEntityId;

    public string CommandId { get; }

    public GAgentDraftRunProjectionContext Context { get; }

    public string ScopeId => RootEntityId;

    public string SessionId => CommandId;
}
