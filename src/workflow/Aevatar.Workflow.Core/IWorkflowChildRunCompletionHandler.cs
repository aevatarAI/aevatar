using Aevatar.Workflow.Abstractions;

namespace Aevatar.Workflow.Core;

internal interface IWorkflowChildRunCompletionHandler
{
    Task<bool> TryHandleAsync(WorkflowCompletedEvent evt, string? publisherActorId, CancellationToken ct);
}
