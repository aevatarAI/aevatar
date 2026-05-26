using Aevatar.CQRS.Core.Abstractions.Commands;

namespace Aevatar.CQRS.Core.Commands;

// Refactor (iter111/cluster-111-handled-dispatch-contract):
//   Old pattern: Public CQRS/runtime surface exposes IActorHandledDispatchPort, lets command paths synchronously wait for one actor turn, then returns DispatchAdmission.
//   New principle: Command skeleton depends only on accepted inbox dispatch; any handled/committed/readmodel stage is modeled as explicit follow-up observation or continuation event, never as dispatch ACK.
public sealed class ActorCommandTargetDispatcher<TTarget>
    : ICommandTargetDispatcher<TTarget>
    where TTarget : class, ICommandDispatchTarget
{
    private readonly IActorDispatchPort _dispatchPort;

    public ActorCommandTargetDispatcher(IActorDispatchPort dispatchPort)
    {
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
    }

    public Task<DispatchAdmission> DispatchAsync(
        TTarget target,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(envelope);

        return _dispatchPort.DispatchAsync(target.TargetId, envelope, ct);
    }
}
