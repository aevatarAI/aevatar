using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

public interface IWorkflowRunRequestExecutor
{
    Task ExecuteAsync(
        IActor actor,
        string actorId,
        EventEnvelope requestEnvelope,
        IWorkflowRunEventSink sink,
        CancellationToken ct = default);
}
