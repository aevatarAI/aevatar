using Aevatar.GAgentService.Abstractions.Responses;

namespace Aevatar.GAgentService.Application.Responses;

public interface ILlmRunExecutionScheduler
{
    ValueTask ScheduleAsync(
        LlmRunExecutorRequest request,
        CancellationToken ct = default);
}
