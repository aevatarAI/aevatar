using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

public sealed class WorkflowRunOutputStreamer
    : IWorkflowRunOutputStreamer,
      IEventOutputStream<WorkflowRunEventEnvelope, WorkflowRunEventEnvelope>,
      IEventFrameMapper<WorkflowRunEventEnvelope, WorkflowRunEventEnvelope>
{
    public async Task StreamAsync(
        IEventSink<WorkflowRunEventEnvelope> sink,
        Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask> emitAsync,
        CancellationToken ct = default)
    {
        await PumpAsync(
            sink.ReadAllAsync(ct),
            emitAsync,
            IsTerminal,
            ct);
    }

    public async Task PumpAsync(
        IAsyncEnumerable<WorkflowRunEventEnvelope> events,
        Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask> emitAsync,
        Func<WorkflowRunEventEnvelope, bool>? shouldStop = null,
        CancellationToken ct = default)
    {
        await foreach (var evt in events.WithCancellation(ct))
        {
            await emitAsync(Map(evt), ct);
            if (shouldStop?.Invoke(evt) == true)
                break;
        }
    }

    public WorkflowRunEventEnvelope Map(WorkflowRunEventEnvelope evt) => evt;

    private static bool IsTerminal(WorkflowRunEventEnvelope evt)
    {
        return evt.EventCase is WorkflowRunEventEnvelope.EventOneofCase.RunFinished
            or WorkflowRunEventEnvelope.EventOneofCase.RunError;
    }
}
