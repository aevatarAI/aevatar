using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Projection.Orchestration;

namespace Aevatar.Workflow.Application.Runs;

public interface IWorkflowRunRequestExecutor
{
    Task ExecuteAsync(
        IActor actor,
        string actorId,
        string runId,
        EventEnvelope requestEnvelope,
        IWorkflowRunEventSink sink,
        CancellationToken ct = default);
}
