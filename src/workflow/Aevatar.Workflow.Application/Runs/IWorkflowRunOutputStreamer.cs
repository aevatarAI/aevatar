using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

public interface IWorkflowRunOutputStreamer
{
    Task StreamAsync(
        IEventSink<WorkflowRunEventEnvelope> sink,
        Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask> emitAsync,
        CancellationToken ct = default);

    WorkflowRunEventEnvelope Map(WorkflowRunEventEnvelope evt);
}
