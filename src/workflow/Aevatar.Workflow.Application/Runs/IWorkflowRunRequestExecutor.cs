using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

public interface IWorkflowRunRequestExecutor
{
    Task ExecuteAsync(
        IActor actor,
        string actorId,
        EventEnvelope requestEnvelope,
        IEventSink<WorkflowRunEvent>? sink,
        CancellationToken ct = default);
}
