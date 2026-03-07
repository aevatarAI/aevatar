using Aevatar.Workflow.Abstractions;

namespace Aevatar.Workflow.Core;

/// <summary>
/// Stateless workflow primitive handler. One handler instance processes one step request.
/// </summary>
public interface IWorkflowPrimitiveExecutor
{
    string Name { get; }

    Task HandleAsync(
        StepRequestEvent request,
        WorkflowPrimitiveExecutionContext context,
        CancellationToken ct);
}
