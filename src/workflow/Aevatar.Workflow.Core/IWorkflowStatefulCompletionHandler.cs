using Aevatar.Workflow.Abstractions;

namespace Aevatar.Workflow.Core;

internal interface IWorkflowStatefulCompletionHandler
{
    Task<bool> TryHandleCompletionAsync(StepCompletedEvent evt, CancellationToken ct);
}
