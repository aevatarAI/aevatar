using Aevatar.CQRS.Core.Abstractions.Commands;

namespace Aevatar.CQRS.Core.Commands;

public sealed class HandledActorCommandTargetDispatcher<TTarget>
    : ICommandTargetDispatcher<TTarget>
    where TTarget : class, ICommandDispatchTarget
{
    private readonly IActorHandledDispatchPort _dispatchPort;

    public HandledActorCommandTargetDispatcher(IActorHandledDispatchPort dispatchPort)
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

        return _dispatchPort.DispatchAndWaitHandledAsync(target.TargetId, envelope, ct);
    }
}
