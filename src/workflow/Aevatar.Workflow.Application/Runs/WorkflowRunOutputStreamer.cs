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
        await PumpAsync(
            sink.ReadAllAsync(ct),
            emitAsync,
            evt => IsTerminalForRun(evt, runId),
            ct);
    }

    public async Task PumpAsync(
        IAsyncEnumerable<WorkflowRunEvent> events,
        Func<WorkflowOutputFrame, CancellationToken, ValueTask> emitAsync,
        Func<WorkflowRunEvent, bool>? shouldStop = null,
        CancellationToken ct = default)
    {
        await foreach (var evt in events.WithCancellation(ct))
        {
            await emitAsync(Map(evt), ct);
            if (shouldStop?.Invoke(evt) == true)
                break;
        }
    }

    public WorkflowOutputFrame Map(WorkflowRunEvent evt) => WorkflowOutputFrameMapper.Map(evt);

    private static bool IsTerminalForRun(WorkflowRunEvent evt, string runId)
    {
        return evt switch
        {
            WorkflowRunFinishedEvent finished => string.Equals(finished.RunId, runId, StringComparison.Ordinal),
            WorkflowRunErrorEvent error => string.Equals(error.RunId, runId, StringComparison.Ordinal),
            _ => false,
        };
    }
}
