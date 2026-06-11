using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.AGUI.Contracts;

namespace Aevatar.GAgentService.Projection.Orchestration;

// Refactor (iter367/cluster-issue377): Old pattern: runtime lease implemented IProjectionPortSessionLease.
// Refactor (iter367/cluster-issue377): Old pattern: ScopeId was an alias for Context.RootActorId.
// Refactor (iter367/cluster-issue377): New principle: typed Context carries RootActorId and SessionId.
// Refactor (iter367/cluster-issue377): New principle: lifecycle routing reads Context directly.
public sealed class GAgentDraftRunRuntimeLease
    : EventSinkProjectionRuntimeLeaseBase<AGUIEvent>,
      IGAgentDraftRunProjectionLease,
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

    public string SessionId => CommandId;
}
