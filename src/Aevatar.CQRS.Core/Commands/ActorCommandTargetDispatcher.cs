using Aevatar.CQRS.Core.Abstractions.Commands;

namespace Aevatar.CQRS.Core.Commands;

public sealed class ActorCommandTargetDispatcher<TTarget>
    : ICommandTargetDispatcher<TTarget>
    where TTarget : class, IActorCommandDispatchTarget
{
    private readonly IActorDispatchPort _dispatchPort;

    public ActorCommandTargetDispatcher(IActorDispatchPort dispatchPort)
    {
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
    }

    public Task DispatchAsync(
        TTarget target,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(envelope);

        return _dispatchPort.DispatchAsync(target.TargetId, envelope, ct);
    }
}
