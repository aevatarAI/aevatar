using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.AGUI.Contracts;

namespace Aevatar.GAgentService.Projection.Orchestration;

/// <summary>
/// Runtime lease for an actorized script service AGUI Projection Pipeline session.
/// It exposes the session context and typed sink handle returned by the
/// projection runtime for activation and release.
/// </summary>
// Refactor (iter367/cluster-issue377): Old pattern: runtime lease implemented IProjectionPortSessionLease.
// Refactor (iter367/cluster-issue377): Old pattern: ScopeId duplicated Context.RootActorId as an alias.
// Refactor (iter367/cluster-issue377): New principle: Context is the single session routing source.
// Refactor (iter367/cluster-issue377): New principle: attach uses RootActorId + SessionId from Context.
public sealed class ScriptServiceAguiRuntimeLease
    : EventSinkProjectionRuntimeLeaseBase<AGUIEvent>,
      IScriptServiceAguiProjectionLease,
      IProjectionContextRuntimeLease<ScriptServiceAguiProjectionContext>
{
    public ScriptServiceAguiRuntimeLease(ScriptServiceAguiProjectionContext context)
        : base(context?.RootActorId ?? throw new ArgumentNullException(nameof(context)))
    {
        Context = context;
        RunId = context.SessionId;
    }

    public string ActorId => RootEntityId;

    public string RunId { get; }

    public ScriptServiceAguiProjectionContext Context { get; }

    public string SessionId => RunId;
}
