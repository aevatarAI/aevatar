using Aevatar.Foundation.Abstractions;

namespace Aevatar.CQRS.Core.Abstractions.Commands;

public interface ICommandDispatchTarget
{
    string TargetId { get; }
}

public interface IActorCommandDispatchTarget : ICommandDispatchTarget
{
    IActor Actor { get; }
}
