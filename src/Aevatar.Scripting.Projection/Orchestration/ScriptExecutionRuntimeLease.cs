using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions.Queries;

namespace Aevatar.Scripting.Projection.Orchestration;

public sealed class ScriptExecutionRuntimeLease
    : EventSinkProjectionRuntimeLeaseBase<EventEnvelope>,
      IScriptExecutionProjectionLease,
      IProjectionPortSessionLease,
      IProjectionContextRuntimeLease<ScriptExecutionProjectionContext>
{
    public ScriptExecutionRuntimeLease(ScriptExecutionProjectionContext context)
        : base(context.RootActorId)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public string ActorId => RootEntityId;

    public ScriptExecutionProjectionContext Context { get; }

    public string ScopeId => RootEntityId;

    public string SessionId => RootEntityId;
}
