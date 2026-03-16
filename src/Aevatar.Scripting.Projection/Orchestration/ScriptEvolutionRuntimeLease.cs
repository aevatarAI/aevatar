using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Evolution;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.Scripting.Projection.Orchestration;

public sealed class ScriptEvolutionRuntimeLease
    : EventSinkProjectionRuntimeLeaseBase<ScriptEvolutionSessionCompletedEvent>,
      IScriptEvolutionProjectionLease,
      IProjectionPortSessionLease,
      IProjectionContextRuntimeLease<ScriptEvolutionSessionProjectionContext>
{
    public ScriptEvolutionRuntimeLease(ScriptEvolutionSessionProjectionContext context)
        : base(context.RootActorId)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        ProposalId = context.ProposalId;
    }

    public string ActorId => RootEntityId;
    public string ProposalId { get; }
    public ScriptEvolutionSessionProjectionContext Context { get; }

    public string ScopeId => RootEntityId;
    public string SessionId => ProposalId;
}
