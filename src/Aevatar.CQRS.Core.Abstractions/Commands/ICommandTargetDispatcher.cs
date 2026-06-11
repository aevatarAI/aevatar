namespace Aevatar.CQRS.Core.Abstractions.Commands;

public interface ICommandTargetDispatcher<in TTarget>
    where TTarget : class, ICommandDispatchTarget
{
    Task<DispatchAdmission> DispatchAsync(
        TTarget target,
        EventEnvelope envelope,
        CancellationToken ct = default);
}
