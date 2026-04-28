using Aevatar.GAgentService.Abstractions.Queries;

namespace Aevatar.GAgentService.Abstractions.Ports;

/// <summary>
/// Read contract for the implementation-agnostic service-run registry.
/// Backed by the durable <c>ServiceRunGAgent</c> projection.
/// </summary>
public interface IServiceRunQueryPort
{
    Task<IReadOnlyList<ServiceRunSnapshot>> ListAsync(
        ServiceRunQuery query,
        CancellationToken ct = default);

    Task<ServiceRunSnapshot?> GetByRunIdAsync(
        string scopeId,
        string serviceId,
        string runId,
        CancellationToken ct = default);

    Task<ServiceRunSnapshot?> GetByCommandIdAsync(
        string scopeId,
        string serviceId,
        string commandId,
        CancellationToken ct = default);
}
