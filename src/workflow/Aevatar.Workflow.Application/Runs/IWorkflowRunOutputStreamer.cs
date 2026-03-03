using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

public interface IWorkflowRunOutputStreamer
{
    Task StreamAsync(
        IEventSink<WorkflowRunEvent> sink,
        Func<WorkflowOutputFrame, CancellationToken, ValueTask> emitAsync,
        CancellationToken ct = default);

    WorkflowOutputFrame Map(WorkflowRunEvent evt);
}
