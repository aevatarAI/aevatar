namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Activation contract for one durable materialization scope.
/// </summary>
public interface IProjectionMaterializationActivationService<TLease>
    where TLease : class, IProjectionRuntimeLease
{
    Task<TLease> EnsureAsync(
        ProjectionMaterializationStartRequest request,
        CancellationToken ct = default);
}
