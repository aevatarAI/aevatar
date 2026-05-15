using Aevatar.GAgentService.Abstractions.Queries;

namespace Aevatar.GAgentService.Abstractions.Ports;

public interface ILlmSessionQueryPort
{
    Task<LlmSessionSnapshot?> GetByResponseIdAsync(
        string responseId,
        CancellationToken ct = default);
}
