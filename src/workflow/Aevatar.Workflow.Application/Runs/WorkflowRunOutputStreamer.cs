using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

public sealed class WorkflowRunOutputStreamer
    : IWorkflowRunOutputStreamer,
      IEventOutputStream<WorkflowRunEvent, WorkflowOutputFrame>,
      IEventFrameMapper<WorkflowRunEvent, WorkflowOutputFrame>
{
    public async Task StreamAsync(
        IWorkflowRunEventSink sink,
        string runId,
        Func<WorkflowOutputFrame, CancellationToken, ValueTask> emitAsync,
        CancellationToken ct = default)
    {
        await PumpAsync(sink.ReadAllAsync(ct), runId, emitAsync, ct);
    }

    public async Task PumpAsync(
        IAsyncEnumerable<WorkflowRunEvent> events,
        string executionId,
        Func<WorkflowOutputFrame, CancellationToken, ValueTask> emitAsync,
        CancellationToken ct = default)
    {
        await foreach (var evt in events.WithCancellation(ct))
        {
            await emitAsync(Map(evt), ct);
            if (IsTerminal(evt, executionId))
                break;
        }
    }

    public WorkflowOutputFrame Map(WorkflowRunEvent evt) => WorkflowOutputFrameMapper.Map(evt);

    public bool IsTerminal(WorkflowRunEvent evt, string executionId)
    {
        return evt switch
        {
            WorkflowRunFinishedEvent finished => string.Equals(finished.RunId, executionId, StringComparison.Ordinal),
            WorkflowRunErrorEvent error => string.Equals(error.RunId, executionId, StringComparison.Ordinal),
            _ => false,
        };
    }
}
