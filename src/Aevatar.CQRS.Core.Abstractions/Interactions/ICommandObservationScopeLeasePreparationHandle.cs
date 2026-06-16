namespace Aevatar.CQRS.Core.Abstractions.Interactions;

public interface ICommandObservationScopeLeasePreparationHandle
{
    Task ReleaseAsync(CancellationToken ct = default);
}
