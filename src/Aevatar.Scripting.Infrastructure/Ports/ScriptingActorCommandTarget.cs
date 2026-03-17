using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class ScriptingActorCommandTarget : IActorCommandDispatchTarget
{
    public ScriptingActorCommandTarget(IActor actor)
    {
        Actor = actor ?? throw new ArgumentNullException(nameof(actor));
    }

    public IActor Actor { get; }

    public string TargetId => Actor.Id;
}
