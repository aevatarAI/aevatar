namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Activation contract for one durable materialization scope.
/// </summary>
public interface IProjectionMaterializationActivationService<TLease>
{
    Task<TLease> EnsureAsync(
        ProjectionMaterializationStartRequest request,
        CancellationToken ct = default);
}
