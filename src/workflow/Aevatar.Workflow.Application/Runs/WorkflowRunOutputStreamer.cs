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
        Func<WorkflowOutputFrame, CancellationToken, ValueTask> emitAsync,
        CancellationToken ct = default)
    {
        await PumpAsync(
            sink.ReadAllAsync(ct),
            emitAsync,
            IsTerminal,
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

    private static bool IsTerminal(WorkflowRunEvent evt)
    {
        return evt switch
        {
            WorkflowRunFinishedEvent => true,
            WorkflowRunErrorEvent => true,
            _ => false,
        };
    }
}
