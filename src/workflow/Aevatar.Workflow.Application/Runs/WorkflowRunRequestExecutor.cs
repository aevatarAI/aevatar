using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Projection.Orchestration;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Application.Runs;

public sealed class WorkflowRunRequestExecutor : IWorkflowRunRequestExecutor
{
    private readonly ILogger<WorkflowRunRequestExecutor> _logger;

    public WorkflowRunRequestExecutor(ILogger<WorkflowRunRequestExecutor> logger)
    {
        _logger = logger;
    }

    public async Task ExecuteAsync(
        IActor actor,
        string actorId,
        string runId,
        EventEnvelope requestEnvelope,
        IWorkflowRunEventSink sink,
        CancellationToken ct = default)
    {
        try
        {
            await actor.HandleEventAsync(requestEnvelope, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Workflow execution failed for actor {ActorId}", actorId);
            try
            {
                sink.Push(new WorkflowRunErrorEvent
                {
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Message = "工作流执行异常",
                    RunId = runId,
                    Code = "INTERNAL_ERROR",
                });
            }
            catch (InvalidOperationException)
            {
            }

            sink.Complete();
        }
    }
}
