using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Presentation.AGUI;

namespace Aevatar.GAgentService.Projection.Orchestration;

/// <summary>
/// Runtime lease for an actorized script service AGUI Projection Pipeline session.
/// It exposes the session context and typed sink handle returned by the
/// projection runtime for activation and release.
/// </summary>
public sealed class ScriptServiceAguiRuntimeLease
    : EventSinkProjectionRuntimeLeaseBase<AGUIEvent>,
      IScriptServiceAguiProjectionLease,
      IProjectionPortSessionLease,
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

    public string ScopeId => RootEntityId;

    public string SessionId => RunId;
}
