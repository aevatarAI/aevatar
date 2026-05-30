using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Evolution;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.Scripting.Projection.Orchestration;

// Refactor (issue-377): Old pattern: runtime lease implemented IProjectionPortSessionLease.
// Refactor (issue-377): Old pattern: ScopeId aliased the session root actor id.
// Refactor (issue-377): New principle: typed evolution session context owns route identity.
// Refactor (issue-377): New principle: leases expose domain contract fields without alias state.
public sealed class ScriptEvolutionRuntimeLease
    : EventSinkProjectionRuntimeLeaseBase<ScriptEvolutionSessionCompletedEvent>,
      IScriptEvolutionProjectionLease,
      IProjectionContextRuntimeLease<ScriptEvolutionSessionProjectionContext>
{
    public ScriptEvolutionRuntimeLease(ScriptEvolutionSessionProjectionContext context)
        : base(context.RootActorId)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        ProposalId = context.SessionId;
    }

    public string ActorId => RootEntityId;
    public string ProposalId { get; }
    public ScriptEvolutionSessionProjectionContext Context { get; }

    public string SessionId => ProposalId;
}
