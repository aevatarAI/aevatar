namespace Aevatar.GAgentService.Abstractions.Ports;

/// <summary>
/// Activation port for the durable service-run current-state projection.
/// Mirrors <see cref="IServiceCatalogProjectionPort"/> shape but scoped to service-run actors.
/// </summary>
public interface IServiceRunCurrentStateProjectionPort
{
    Task EnsureProjectionAsync(string actorId, CancellationToken ct = default);
}
