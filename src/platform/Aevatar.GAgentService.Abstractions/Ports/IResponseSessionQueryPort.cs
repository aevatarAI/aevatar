using Aevatar.GAgentService.Abstractions.Queries;

namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IResponseSessionQueryPort
{
    Task<ResponseSessionSnapshot?> GetByResponseIdAsync(
        string responseId,
        CancellationToken ct = default);
}
