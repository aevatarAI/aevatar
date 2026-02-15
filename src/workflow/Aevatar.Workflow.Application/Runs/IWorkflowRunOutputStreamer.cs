using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Projection.Orchestration;

namespace Aevatar.Workflow.Application.Runs;

public interface IWorkflowRunOutputStreamer
{
    Task StreamAsync(
        IWorkflowRunEventSink sink,
        string runId,
        Func<WorkflowOutputFrame, CancellationToken, ValueTask> emitAsync,
        CancellationToken ct = default);
}
