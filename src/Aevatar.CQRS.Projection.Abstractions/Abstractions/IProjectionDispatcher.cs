namespace Aevatar.CQRS.Projection.Abstractions;

/// <summary>
/// Unified projection dispatch entry for one envelope.
/// </summary>
public interface IProjectionDispatcher<in TContext>
{
    Task DispatchAsync(TContext context, EventEnvelope envelope, CancellationToken ct = default);
}
